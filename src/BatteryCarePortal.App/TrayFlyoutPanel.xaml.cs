using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Portal.Core.Battery;
using Portal.Core.Theming;
using Portal.Core.Tray;
using VirtualKey = Windows.System.VirtualKey;

namespace BatteryCarePortal;

/// <summary>
/// The flyout content: a pinned header, a scrolling grid of tiles and a pinned footer. The charge-limit switch and Top up
/// call the same <see cref="ChargeLimitController"/> methods as the Charging page and are gated the same way; the tile
/// order and visibility are the only things written here (through the settings store).
/// </summary>
public sealed partial class TrayFlyoutPanel : UserControl
{
    private sealed class Tile
    {
        public string Id = "";
        public bool Wide;
        public bool Available;
        public ContentControl Host = null!;
        public Border Card = null!;
        public TextBlock Title = null!, Value = null!, Caption = null!;
        public ToggleSwitch ShowSwitch = null!;
        public UIElement? Extra;
        public string TitleText = "";
    }

    private readonly Dictionary<string, Tile> _tiles = new();
    private readonly ToggleSwitch _limitSwitch = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly ProgressRing _busyRing = new() { IsActive = false, Width = 20, Height = 20 };
    private readonly TextBlock _failedText = new();
    private readonly Button _topUpButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _stopTopUpButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DispatcherQueueTimer _timer;
    private TrayTileLayout _layout = TrayTileLayout.Default();
    private bool _shown;
    private bool _busy;
    private bool _updating;
    private bool _gateAllowed;
    private int _refreshing;
    private bool _again;

    /// <summary>Raised on the UI thread when the content may have changed size.</summary>
    public event Action? ContentChanged;

    /// <summary>Raised when the user asks for the main window.</summary>
    public event Action? OpenRequested;

    /// <summary>Raised when Esc is pressed outside edit mode.</summary>
    public event Action? CloseRequested;

    /// <summary>Edit mode keeps the pop-up open when it loses focus.</summary>
    public bool IsEditing { get; private set; }

    /// <summary>Only a visible flyout refreshes on its own (every 1.5 s and on battery events).</summary>
    public bool IsShown
    {
        get => _shown;
        set
        {
            _shown = value;
            if (value) _timer.Start(); else _timer.Stop();
        }
    }

    public TrayFlyoutPanel()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1500);
        _timer.Tick += (_, _) => RefreshIfShown();

        OpenButton.Content = Strings.Get("TrayOpenApp");
        AutomationProperties.SetName(OpenButton, Strings.Get("TrayOpenApp"));
        _topUpButton.Content = Strings.Get("FlyoutTopUp");
        _topUpButton.Style = (Style)Resources["TrayAccentButton"];
        _topUpButton.Click += OnTopUp;
        AutomationProperties.SetName(_topUpButton, Strings.Get("FlyoutTopUp"));
        _stopTopUpButton.Content = Strings.Get("FlyoutStopTopUp");
        _stopTopUpButton.Click += OnStopTopUp;
        AutomationProperties.SetName(_stopTopUpButton, Strings.Get("FlyoutStopTopUp"));
        _limitSwitch.Toggled += OnLimitToggled;
        AutomationProperties.SetName(_limitSwitch, Strings.Get("FlyoutLimitTitle"));
        _failedText.Style = (Style)Resources["TrayTileError"];
        _failedText.Visibility = Visibility.Collapsed;

        var limitExtra = new StackPanel { Spacing = 6 };
        limitExtra.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _busyRing, _limitSwitch } });
        limitExtra.Children.Add(_failedText);
        var topUpExtra = new StackPanel { Spacing = 6 };
        topUpExtra.Children.Add(_topUpButton);
        topUpExtra.Children.Add(_stopTopUpButton);

        MakeTile(TrayTileLayout.Battery, "TrayTileBattery", wide: false, null);
        MakeTile(TrayTileLayout.Limit, "FlyoutLimitTitle", wide: true, limitExtra);
        MakeTile(TrayTileLayout.TopUp, "TrayTileTopUp", wide: false, topUpExtra);
        MakeTile(TrayTileLayout.Heat, "TrayTileHeat", wide: false, null);
        MakeTile(TrayTileLayout.Health, "TrayTileHealth", wide: false, null);

        EditButton.Content = Strings.Get("TrayEditWidgets");
        AutomationProperties.SetName(EditButton, Strings.Get("TrayEditWidgets"));
        KeyDown += OnPanelKeyDown;

        AppServices.Battery.SnapshotChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshIfShown);
        AppServices.SupportChanged += RefreshIfShown;
        AppServices.ChargeStateChanged += RefreshIfShown;
        AppServices.ChargeWriteChanged += () =>
        {
            UpdateEnabled();
            RefreshIfShown();
        };
    }

    private void MakeTile(string id, string titleKey, bool wide, UIElement? extra)
    {
        var tile = new Tile { Id = id, Wide = wide, Extra = extra, TitleText = Strings.Get(titleKey) };
        tile.Title = new TextBlock { Style = (Style)Resources["TrayTileTitle"], Text = tile.TitleText };
        tile.Value = new TextBlock { Style = (Style)Resources["TrayTileValue"] };
        tile.Caption = new TextBlock { Style = (Style)Resources["TrayTileCaption"] };
        tile.ShowSwitch = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(tile.ShowSwitch, string.Format(Strings.Get("TrayShowTile"), tile.TitleText));
        tile.ShowSwitch.Toggled += (_, _) =>
        {
            if (_updating) return;
            _layout.SetVisible(id, tile.ShowSwitch.IsOn);
            tile.Card.Opacity = tile.ShowSwitch.IsOn ? 1 : 0.5;
            SaveLayout();
        };

        var grid = new Grid { RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(tile.Title);
        Grid.SetColumn(tile.ShowSwitch, 1);
        grid.Children.Add(tile.ShowSwitch);
        Grid.SetRow(tile.Value, 1);
        Grid.SetColumnSpan(tile.Value, 2);
        grid.Children.Add(tile.Value);
        Grid.SetRow(tile.Caption, 2);
        Grid.SetColumnSpan(tile.Caption, 2);
        grid.Children.Add(tile.Caption);
        if (extra is FrameworkElement fe)
        {
            fe.Margin = new Thickness(0, 8, 0, 0);
            fe.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(fe, 3);
            Grid.SetColumnSpan(fe, 2);
            grid.Children.Add(fe);
        }

        tile.Card = new Border { Style = (Style)Resources["TrayTileCard"], Child = grid };
        tile.Host = new ContentControl
        {
            Content = tile.Card,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            Tag = id,
        };
        _tiles[id] = tile;
    }

    // ---- appearance, layout, edit mode ----

    /// <summary>Theme follows the Appearance setting; the pane is 82 % over a backdrop and solid in high contrast or without one.</summary>
    public void ApplyAppearance(AppearanceSettings appearance, bool solid)
    {
        RequestedTheme = appearance.Theme switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        PaneFill.Opacity = solid ? 1 : 0.82;
        double radius = appearance.Corners == CornerChoice.Square ? 0 : 8;
        PaneRoot.CornerRadius = new CornerRadius(radius);
        PaneFill.CornerRadius = new CornerRadius(radius);
    }

    /// <summary>Called when the pop-up opens: reads the saved tile order and leaves edit mode.</summary>
    public void PrepareToShow()
    {
        try { _layout = TrayTileLayout.Parse(AppServices.Settings.Load().TrayTiles); }
        catch (Exception) { _layout = TrayTileLayout.Default(); }
        IsEditing = false;
        ApplyEditState();
        RebuildGrid();
    }

    private void SaveLayout()
    {
        string text = _layout.Serialize();
        _ = Task.Run(() =>
        {
            try { AppServices.Settings.Update(s => s.TrayTiles = text); }
            catch (Exception) { }
        });
    }

    private void ApplyEditState()
    {
        EditButton.Content = Strings.Get(IsEditing ? "TrayDoneEditing" : "TrayEditWidgets");
        _updating = true;
        foreach (Tile t in _tiles.Values)
        {
            bool visible = _layout.IsVisible(t.Id);
            t.ShowSwitch.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
            t.ShowSwitch.IsOn = visible;
            t.Card.Opacity = IsEditing && !visible ? 0.5 : 1;
            if (t.Extra is not null) t.Extra.Visibility = IsEditing ? Visibility.Collapsed : Visibility.Visible;
        }
        _updating = false;
    }

    private void RebuildGrid()
    {
        TileGrid.Children.Clear();
        TileGrid.RowDefinitions.Clear();
        int row = -1, col = 2;
        foreach ((string id, bool visible) in _layout.Items)
        {
            if (!_tiles.TryGetValue(id, out Tile? t) || !t.Available || (!visible && !IsEditing)) continue;
            if (t.Wide || col >= 2)
            {
                TileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row++;
                col = 0;
            }
            Grid.SetRow(t.Host, row);
            Grid.SetColumn(t.Host, col);
            Grid.SetColumnSpan(t.Host, t.Wide ? 2 : 1);
            col += t.Wide ? 2 : 1;
            TileGrid.Children.Add(t.Host);
        }
        ContentChanged?.Invoke();
    }

    private void OnEditToggle(object sender, RoutedEventArgs e) => SetEditing(!IsEditing);

    private void SetEditing(bool on)
    {
        IsEditing = on;
        ApplyEditState();
        RebuildGrid();
    }

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (IsEditing) SetEditing(false);
            else CloseRequested?.Invoke();
        }
        else if (ctrl && IsEditing && (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down) && FocusedTile() is { } tile)
        {
            e.Handled = true;
            string[] shown = _tiles.Values.Where(t => t.Available).Select(t => t.Id).ToArray();
            if (_layout.Move(tile.Id, e.Key == VirtualKey.Up ? -1 : 1, shown))
            {
                RebuildGrid();
                SaveLayout();
                tile.Host.Focus(FocusState.Keyboard);
            }
        }
    }

    private Tile? FocusedTile()
    {
        DependencyObject? node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (node is not null)
        {
            if (node is ContentControl { Tag: string id } && _tiles.TryGetValue(id, out Tile? t)) return t;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>First tile gets the focus when the pop-up opens, so Up/Down work straight away.</summary>
    public void FocusFirst()
    {
        foreach ((string id, _) in _layout.Items)
            if (_tiles[id].Host.Parent is not null && _tiles[id].Host.Focus(FocusState.Keyboard)) return;
        OpenButton.Focus(FocusState.Keyboard);
    }

    // ---- live values ----

    private void RefreshIfShown()
    {
        if (IsShown) Refresh();
    }

    public async void Refresh()
    {
        // One refresh at a time; a change that lands meanwhile is picked up by the next event.
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) { _again = true; return; }
        try
        {
            BatterySnapshot s = AppServices.Battery.Current;
            DeviceSupport? support = AppServices.Support;
            ChargeLimitController? controller = AppServices.Charge;
            bool busy = _busy || controller?.IsBusy == true;

            ChargeStatusView? view = null;
            ChargeLimitState? state = null;
            WriteGateResult? gate = null;
            HeatProtectionModel? heat = null;
            bool heatOn = false;
            if (s.Present)
            {
                (view, state, gate, heat, heatOn) = await Task.Run(() =>
                {
                    bool hasSupport = support is not null;
                    HeatProtectionSettings? settings = null;
                    try { settings = AppServices.Settings.LoadLastGood().HeatProtection; } catch (Exception) { }
                    return (
                        hasSupport ? ChargeStatusView.Build(null, busy) : null,
                        hasSupport ? controller?.ReadState() : null,
                        hasSupport ? controller?.EvaluateGate() : null,
                        settings is null ? null : HeatProtectionModel.Build(s, settings, controller?.Capabilities),
                        settings?.Enabled == true);
                });
            }

            DeviceText.Text = support is null ? string.Empty : DeviceNameText.Join(support.Device.Manufacturer,
                support.Device.Family.Length > 0 ? support.Device.Family : support.Device.Model);
            DeviceText.Visibility = DeviceText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            string stateWord = Strings.Get(s.IsCharging ? "TrayStateCharging" : s.IsOnAc ? "TrayStatePlugged" : "TrayStateBattery");
            string percentText = s.Percent is int p ? BatteryFormat.Percent(p, format: "0") : BatteryFormat.None;
            HeaderText.Text = HeaderFor(s, view, state, support, stateWord);

            bool showLimit = s.Present && controller is not null && state is not null && ReferenceEquals(controller, AppServices.Charge);

            _tiles[TrayTileLayout.Battery].Available = s.Present;
            SetTile(TrayTileLayout.Battery, percentText, stateWord, s.IsCharging);

            _tiles[TrayTileLayout.Limit].Available = showLimit;
            if (showLimit)
            {
                _gateAllowed = gate?.Allowed == true;
                int? limit = state!.Limit;
                bool on = limit is int l && l < 100;
                _updating = true;
                _limitSwitch.IsOn = on;
                _updating = false;
                _busyRing.IsActive = _busy;
                SetTile(TrayTileLayout.Limit, on ? BatteryFormat.Percent(limit!.Value, format: "0") : Strings.Get("TrayValueOff"),
                    on ? string.Format(Strings.Get("FlyoutLimitOn"), limit) : Strings.Get("FlyoutLimitOff"), on);
            }

            bool canStart = showLimit && view?.CanStartTopUp == true, canStop = showLimit && view?.CanStopTopUp == true;
            _tiles[TrayTileLayout.TopUp].Available = canStart || canStop;
            _topUpButton.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
            _stopTopUpButton.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
            SetTile(TrayTileLayout.TopUp, Strings.Get(canStop ? "TrayTopUpRunning" : "TrayTopUpReady"),
                canStop && view is not null ? view.Sentence : string.Empty, canStop);

            bool heatShown = s.Present && heat is { SwitchOffered: true };
            _tiles[TrayTileLayout.Heat].Available = heatShown;
            if (heatShown)
            {
                string temp = heat!.TemperatureC is double t ? $"{t:0} °C" : BatteryFormat.None;
                SetTile(TrayTileLayout.Heat, temp,
                    Strings.Get(heat.Zone == HeatZone.Hot ? "TrayHeatHot" : heatOn ? "TrayHeatOn" : "TrayHeatOff"), heatOn);
            }

            _tiles[TrayTileLayout.Health].Available = s.Present && s.HealthPercent is not null;
            if (s.HealthPercent is double h)
                SetTile(TrayTileLayout.Health, BatteryFormat.Percent((int)Math.Round(h), format: "0"),
                    s.CycleCount is int cycles ? string.Format(Strings.Get("TrayHealthCycles"), cycles) : string.Empty, false);

            UpdateEnabled();
            string key = string.Concat(_tiles.Values.Select(t => t.Available ? '1' : '0'));
            if (key != _availKey)
            {
                _availKey = key;
                RebuildGrid();
            }
        }
        catch (Exception) { }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
            try { ContentChanged?.Invoke(); } catch (Exception) { }
            if (_again) { _again = false; Refresh(); }
        }
    }

    // The set of tiles that exist decides the grid; values changing do not rebuild it.
    private string _availKey = "";

    private string HeaderFor(BatterySnapshot s, ChargeStatusView? view, ChargeLimitState? state, DeviceSupport? support, string stateWord)
    {
        if (!s.Present) return Strings.Get("FlyoutNoBattery");
        if (view is null || support is null) return stateWord;
        int limit = state?.Limit ?? view.Limit ?? 100;
        return view.Kind switch
        {
            ChargeStatusKind.On when s.IsCharging => string.Format(Strings.Get("TrayHeaderCharging"), limit),
            ChargeStatusKind.On when s.IsOnAc && s.Percent is int pc && pc >= limit - 1 => string.Format(Strings.Get("TrayHeaderHolding"), limit),
            ChargeStatusKind.On => string.Format(Strings.Get("TrayHeaderOn"), stateWord),
            ChargeStatusKind.Off => string.Format(Strings.Get("TrayHeaderOff"), stateWord),
            ChargeStatusKind.TopUp => string.Format(Strings.Get("TrayHeaderTopUp"), stateWord),
            _ => view.Sentence,
        };
    }

    private void SetTile(string id, string value, string caption, bool selected)
    {
        Tile t = _tiles[id];
        t.Value.Text = value;
        t.Caption.Text = caption;
        t.Caption.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(t.Host, caption.Length > 0 ? $"{t.TitleText}, {value}, {caption}" : $"{t.TitleText}, {value}");
        if (selected)
        {
            Windows.UI.Color accent = AccentColor();
            t.Card.Background = new SolidColorBrush(accent) { Opacity = 0.18 };
            t.Card.BorderBrush = new SolidColorBrush(accent);
        }
        else
        {
            t.Card.ClearValue(Border.BackgroundProperty);
            t.Card.ClearValue(Border.BorderBrushProperty);
        }
    }

    private static Windows.UI.Color AccentColor()
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? v) && v is Windows.UI.Color c) return c;
            return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch (Exception) { return Windows.UI.Color.FromArgb(255, 0, 120, 212); }
    }

    // Stop top-up does not need the gate: the controller answers, and a refusal is shown.
    private void UpdateEnabled()
    {
        bool idle = !_busy && AppServices.Charge is { IsBusy: false };
        _limitSwitch.IsEnabled = _topUpButton.IsEnabled = idle && _gateAllowed;
        _stopTopUpButton.IsEnabled = idle;
    }

    // ---- charge actions (unchanged calls) ----

    private void OnLimitToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _busy) return;
        bool on = _limitSwitch.IsOn;
        Run(async controller =>
        {
            int? value = ChargeLimitChoice.PreferredValue(controller.Capabilities, controller.ReadState(), controller.LoadIntent());
            return await controller.SetLimitAsync(on, value);
        });
    }

    /// <summary>The tray icon menu's Charge limit item: flips the limit with the same controller call as the switch.</summary>
    public void ToggleLimitFromMenu()
    {
        Run(async controller =>
        {
            ChargeLimitState current = controller.ReadState();
            bool on = !(current.Limit is int l && l < 100);
            int? value = ChargeLimitChoice.PreferredValue(controller.Capabilities, current, controller.LoadIntent());
            return await controller.SetLimitAsync(on, value);
        });
    }

    private void OnTopUp(object sender, RoutedEventArgs e) => Run(c => c.StartTopUpAsync(TopUpPolicy.MaxDuration));

    private void OnStopTopUp(object sender, RoutedEventArgs e) => Run(c => c.EndTopUpAsync());

    // Same controller methods as the Charging page; one change at a time.
    private async void Run(Func<ChargeLimitController, Task<ChargeActionResult>> action)
    {
        if (_busy || AppServices.Charge is not { } controller) return;
        _busy = true;
        _failedText.Visibility = Visibility.Collapsed;
        _busyRing.IsActive = true;
        UpdateEnabled();
        try
        {
            ChargeActionResult result = await Task.Run(() => action(controller));
            if (result.Succeeded) AppServices.ClearReapplyNotice();
            ShowFailure(ChargeResultNotices.For(result) switch
            {
                ChargeResultNotice.None => null,
                ChargeResultNotice.Busy => Strings.Get("FlyoutBusy"),
                ChargeResultNotice.Refused => ChargeStatusView.RefusalText(result.Refusal, result.Message),
                ChargeResultNotice.Message => result.Message,
                ChargeResultNotice.NotChanged => Strings.Get("FlyoutLimitFailed"),
                _ => Strings.Get("FlyoutMayHaveChanged"),
            });
        }
        catch (Exception)
        {
            ShowFailure(Strings.Get("FlyoutMayHaveChanged"));
        }
        finally
        {
            _busy = false;
            _busyRing.IsActive = false;
            UpdateEnabled();
            AppServices.NotifyChargeChanged();
            Refresh();
        }
    }

    private void ShowFailure(string? text)
    {
        if (text is null) return;
        _failedText.Text = text;
        _failedText.Visibility = Visibility.Visible;
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
}
