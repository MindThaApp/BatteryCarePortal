using System.Globalization;
using BatteryCarePortal.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Portal.Core.Battery;
using Portal.Core.Services;

namespace BatteryCarePortal.Pages;

/// <summary>Reads the recorded history and shows it; it never writes, moves or deletes any history file.</summary>
public sealed partial class HistoryPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly Card _level = new("HistoryLevelTitle");
    private readonly Card _watts = new("HistoryWattsTitle");
    private readonly Card _health = new("HistoryHealthTitle");
    private readonly IReadOnlyList<HistoryRange> _ranges = HistoryShaping.AvailableRanges(FeatureTiers.HistoryWindowDays(hasPro: false));
    private HistoryRange _range = HistoryRange.Day;
    private bool _building;
    private int _version;

    public HistoryPage()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(Column);
        LayoutHelper.AlignToStart(SimulatedChip);

        _building = true;
        foreach (HistoryRange r in _ranges) RangeButtons.Items.Add(Strings.Get(RangeKey(r)));
        RangeButtons.SelectedIndex = 0;
        RangeButtons.Visibility = _ranges.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _building = false;

        Cards.Children.Add(_level.Root);
        Cards.Children.Add(_watts.Root);
        Cards.Children.Add(_health.Root);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { _timer.Start(); _ = RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private static string RangeKey(HistoryRange r) => r switch
    {
        HistoryRange.Day => "HistoryRangeDay",
        HistoryRange.Week => "HistoryRangeWeek",
        _ => "HistoryRangeMonth",
    };

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || RangeButtons.SelectedIndex < 0 || RangeButtons.SelectedIndex >= _ranges.Count) return;
        _range = _ranges[RangeButtons.SelectedIndex];
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            int version = ++_version;
            HistoryRange range = _range;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan span = HistoryShaping.Span(range);
            bool fake = BatteryEnvironment.IsFakeBatteryEnabled;

            (IReadOnlyList<BatteryHistoryEntry> entries, IReadOnlyList<BatteryHealthPoint> healthPoints) =
                await Task.Run(() => (AppServices.History.Read(now - span, now), AppServices.History.ReadHealthTrend()));
            if (version != _version) return;

            bool simulatedEntries = HistoryShaping.UseSimulatedEntries(fake, entries.Count);
            bool simulatedHealth = HistoryShaping.UseSimulatedHealth(fake, healthPoints.Count);
            if (simulatedEntries)
                entries = HistoryShaping.SimulatedEntries(now, span, range == HistoryRange.Day ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30));
            if (simulatedHealth) healthPoints = HistoryShaping.SimulatedHealth(DateOnly.FromDateTime(DateTime.Now));

            SimulatedChip.Visibility = simulatedEntries || simulatedHealth ? Visibility.Visible : Visibility.Collapsed;
            HistorySeries levelSeries = HistoryShaping.Build(entries, range, now, HistoryShaping.Level);
            HistorySeries wattsSeries = HistoryShaping.Build(entries, range, now, HistoryShaping.Watts);

            NoteText.Text = simulatedEntries ? Strings.Get("HistorySimulatedNote")
                : entries.Count > 0 && HistoryShaping.StartedInsideWindow(entries.Min(e => e.Time), levelSeries.From, levelSeries.BucketSize)
                    ? string.Format(Strings.Get("HistoryStartedNote"), entries.Min(e => e.Time).ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
                    : "";
            NoteText.Visibility = NoteText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            ShowLevel(levelSeries);
            ShowWatts(wattsSeries, levelSeries.HasData);
            ShowHealth(HealthTrendModel.Build(healthPoints));
        }
        catch (Exception) { }
    }

    private static string RangeName(HistoryRange r) => Strings.Get(RangeKey(r)).ToLowerInvariant();

    private static string AxisLabel(DateTimeOffset t, HistoryRange range) =>
        t.ToLocalTime().ToString(range == HistoryRange.Day ? "t" : "m", CultureInfo.CurrentCulture);

    private static string FullTime(DateTimeOffset t) => t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static double X(HistorySeries s, HistoryBucket b) =>
        (b.Start + s.BucketSize / 2 - s.From) / (s.To - s.From);

    private static string Range(HistoryBucket b, Func<double?, string> format) =>
        b.Min == b.Max ? "" : $" ({format(b.Min)} – {format(b.Max)})";

    private void ShowLevel(HistorySeries s)
    {
        string Pct(double? v) => BatteryFormat.Percent(v);
        if (!s.HasData) { _level.ShowMessage(Strings.Get("HistoryNoData")); return; }
        var lines = s.Segments().Select(seg => (IReadOnlyList<PlotPoint>)seg.Buckets
            .Select(b => new PlotPoint(X(s, b), b.Average!.Value, $"{FullTime(b.Start)} · {Pct(b.Average)}{Range(b, Pct)}")).ToList()).ToList();
        var data = new ChartData(lines, HistoryShaping.LevelScale(), new double[] { 0, 50, 100 }, v => v.ToString("0", CultureInfo.CurrentCulture),
            AxisLabel(s.From, s.Range), AxisLabel(s.From + (s.To - s.From) / 2, s.Range), AxisLabel(s.To, s.Range),
            string.Format(Strings.Get("HistoryLevelSummary"), RangeName(s.Range), Pct(s.Last), Pct(s.Min), Pct(s.Max)));
        _level.ShowChart(data, () => Table(Strings.Get("HistoryLevelTitle"),
            new[] { Strings.Get("HistoryColTime"), Strings.Get("HistoryColAverage"), Strings.Get("HistoryColLowest"), Strings.Get("HistoryColHighest") },
            HistoryShaping.TableRows(s).Select(r => new[] { FullTime(r.Start), Pct(r.Average), Pct(r.Min), Pct(r.Max) })), null);
    }

    private void ShowWatts(HistorySeries s, bool levelHasData)
    {
        string W(double? v) => BatteryFormat.Watts(v);
        string Dir(double? v) => v is > 0 ? " " + Strings.Get("HistoryCharging") : v is < 0 ? " " + Strings.Get("HistoryDischarging") : "";
        if (!s.HasData) { _watts.ShowMessage(Strings.Get(levelHasData ? "HistoryNoWatts" : "HistoryNoData")); return; }
        HistoryScale scale = HistoryShaping.WattsScale(s.Min, s.Max);
        var lines = s.Segments().Select(seg => (IReadOnlyList<PlotPoint>)seg.Buckets
            .Select(b => new PlotPoint(X(s, b), b.Average!.Value, $"{FullTime(b.Start)} · {W(b.Average)}{Dir(b.Average)}{Range(b, W)}")).ToList()).ToList();
        var ticks = new List<double> { scale.Low, scale.High };
        if (scale.Low < 0 && scale.High > 0) ticks.Add(0);
        var data = new ChartData(lines, scale, ticks, v => v.ToString("0", CultureInfo.CurrentCulture),
            AxisLabel(s.From, s.Range), AxisLabel(s.From + (s.To - s.From) / 2, s.Range), AxisLabel(s.To, s.Range),
            string.Format(Strings.Get("HistoryWattsSummary"), RangeName(s.Range), W(s.Last), W(s.Min), W(s.Max)));
        _watts.ShowChart(data, () => Table(Strings.Get("HistoryWattsTitle"),
            new[] { Strings.Get("HistoryColTime"), Strings.Get("HistoryColAverage"), Strings.Get("HistoryColLowest"), Strings.Get("HistoryColHighest") },
            HistoryShaping.TableRows(s).Select(r => new[] { FullTime(r.Start), W(r.Average), W(r.Min), W(r.Max) })), null);
    }

    private void ShowHealth(HealthTrendModel m)
    {
        string Pct(double? v) => BatteryFormat.Percent(v);
        string Day(DateOnly d) => d.ToString("d", CultureInfo.CurrentCulture);
        if (!m.HasData) { _health.ShowMessage(Strings.Get("HistoryNoHealth")); return; }
        string note = Strings.Get("HistoryHealthNote");
        Func<UIElement> table = () => Table(Strings.Get("HistoryHealthTitle"),
            new[] { Strings.Get("HistoryColDate"), Strings.Get("HistoryColHealth"), Strings.Get("HistoryColCycles") },
            m.Points.Reverse().Select(p => new[] { Day(p.Date), Pct(p.HealthPercent), BatteryFormat.Count(p.CycleCount) }));
        if (m.Points.Count < 2)
        {
            _health.ShowMessage(string.Format(Strings.Get("HistoryHealthSingle"), Pct(m.Last), Day(m.Points[0].Date)), note, table);
            return;
        }

        DateOnly first = m.Points[0].Date, last = m.Points[^1].Date;
        var line = m.Points.Select(p => new PlotPoint(m.DayFraction(p.Date), p.HealthPercent,
            $"{Day(p.Date)} · {Pct(p.HealthPercent)}" + (p.CycleCount is int c ? " · " + string.Format(Strings.Get("HistoryCyclesPart"), c).TrimStart(',', ' ') : ""))).ToList();
        string cycles = m.Cycles is int n ? string.Format(Strings.Get("HistoryCyclesPart"), n) : "";
        var data = new ChartData(new[] { (IReadOnlyList<PlotPoint>)line }, HealthTrendModel.Scale(), new double[] { 0, 50, 100 },
            v => v.ToString("0", CultureInfo.CurrentCulture), Day(first), Day(first.AddDays((last.DayNumber - first.DayNumber) / 2)), Day(last),
            string.Format(Strings.Get("HistoryHealthSummary"), Day(first), Pct(m.First), Pct(m.Last), (m.Change ?? 0).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture), cycles));
        _health.ShowChart(data, table, note);
    }

    private static UIElement Table(string name, string[] headers, IEnumerable<string[]> rows)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 2 };
        foreach (string _ in headers) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void AddRow(IEnumerable<string> cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int col = 0;
            foreach (string cell in cells)
            {
                var t = new TextBlock { Text = cell, TextWrapping = TextWrapping.Wrap, FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
                Grid.SetRow(t, row);
                Grid.SetColumn(t, col++);
                grid.Children.Add(t);
            }
            row++;
        }
        AddRow(headers, true);
        foreach (string[] cells in rows) AddRow(cells, false);
        var scroller = new ScrollViewer { MaxHeight = 360, Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(scroller, string.Format(Strings.Get("HistoryTableName"), name));
        return scroller;
    }

    /// <summary>A card with a title, a chart or a message, and a switch between the chart and the same data as a table.</summary>
    private sealed class Card
    {
        private readonly HistoryChartView _chart = new();
        private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        private readonly ToggleButton _toggle = new();
        private readonly ContentControl _table = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        private Func<UIElement>? _buildTable;

        public Card(string titleKey)
        {
            var title = new TextBlock { Text = Strings.Get(titleKey), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(title);
            stack.Children.Add(_message);
            stack.Children.Add(_chart);
            stack.Children.Add(_table);
            stack.Children.Add(_toggle);
            stack.Children.Add(_note);
            _toggle.Click += (_, _) => Apply();
            Root = new Border
            {
                Padding = new Thickness(20),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = stack,
            };
        }

        public Border Root { get; }

        public void ShowMessage(string text, string? note = null, Func<UIElement>? table = null)
        {
            _message.Text = text;
            _buildTable = table;
            _chart.Visibility = Visibility.Collapsed;
            _toggle.Visibility = table is null ? Visibility.Collapsed : Visibility.Visible;
            if (table is null) _toggle.IsChecked = false;
            SetNote(note);
            Apply();
        }

        public void ShowChart(ChartData data, Func<UIElement> table, string? note)
        {
            _message.Text = "";
            _chart.SetData(data);
            _buildTable = table;
            _toggle.Visibility = Visibility.Visible;
            SetNote(note);
            Apply();
        }

        private void SetNote(string? note)
        {
            _note.Text = note ?? "";
            _note.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Apply()
        {
            bool asTable = _toggle.IsChecked == true && _buildTable is not null;
            _toggle.Content = Strings.Get(asTable ? "HistoryShowChart" : "HistoryShowTable");
            _message.Visibility = _message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _table.Visibility = asTable ? Visibility.Visible : Visibility.Collapsed;
            _table.Content = asTable ? _buildTable!() : null;
            _chart.Visibility = !asTable && _message.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
