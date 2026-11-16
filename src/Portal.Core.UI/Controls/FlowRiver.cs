using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Portal.Core.Power;
using Portal.Core.Power.Dashboard;

using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Portal.Core.UI.Controls;

/// <summary>
/// The power river (<see cref="FlowLayout"/>): one continuous stream of soft translucent bands, cut only by thin breaks
/// where the cable runs between two loads. The source on the left with its watts inside, the PC or laptop symbol with the
/// combined load under it and the loss peeling off, the split into the parts with each part's watts written in its stream,
/// and the rounded ends with the parts' names. An open part breaks and splits again into its own parts. Colours run from
/// one stream into the next, with a slow light sweeping downstream while the view is shown and Windows animations are on.
/// The layout is redone for every width, so the river always fills its space; text keeps its normal size.
/// Opening or closing a part, and new readings, glide from the old shapes to the new ones.
/// </summary>
internal sealed class FlowRiver : Grid
{
    private static readonly TimeSpan TweenTime = TimeSpan.FromMilliseconds(380);
    private const double LabelAlong = 0.66;

    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Disabled,
        ZoomMode = ZoomMode.Disabled,
    };
    private readonly Grid _surface = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Canvas _ribbonLayer = new();
    private readonly Canvas _nodeLayer = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 8) };
    private readonly HashSet<string> _expanded = [];
    private readonly Dictionary<string, NodeView> _nodes = [];
    private readonly Dictionary<string, RibbonView> _ribbons = [];
    private readonly LinearGradientBrush _shimmer;
    private readonly TranslateTransform _shimmerMove = new();
    private Storyboard? _shimmerBoard;
    private FlowPalette? _wavePalette;
    private string _waveSpeedKey = string.Empty, _lightKey = string.Empty;
    private double _shownHeight = -1, _heightFrom, _heightTo;
    private PowerNode _root = PowerNode.Unavailable("pc", "Whole PC", PowerCategory.Total);
    private bool _onBattery, _laptop;
    private string? _mainsName;
    private FlowLayoutResult _layout = FlowLayoutResult.Empty;
    private DispatcherQueueTimer? _tween;
    private DateTime _tweenStart;
    private bool _animations = true, _active = true, _hostVisible = true, _inView = true, _running;
    private XamlRoot? _hookedRoot;
    private string _paletteKey = string.Empty;

    /// <summary>Raised after a part was opened or closed, so a list of the streams can follow.</summary>
    public event EventHandler? ExpandedChanged;

    /// <summary>Raised when the battery is selected (the battery as the source, or the battery being charged).</summary>
    public event EventHandler? BatteryRequested;

    public FlowRiver()
    {
        FlowDirection = FlowDirection.LeftToRight;
        _shimmer = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(250, 0),
            SpreadMethod = GradientSpreadMethod.Repeat, Transform = _shimmerMove,
        };
        _surface.Children.Add(_ribbonLayer);
        _surface.Children.Add(_nodeLayer);
        _scroll.Content = _surface;
        Children.Add(_scroll);
        Children.Add(_empty);
        AutomationProperties.SetName(this, Localization.Get("PowerRiverAutomation"));
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) Relayout(animate: false); };
        ActualThemeChanged += (_, _) => { _paletteKey = string.Empty; Relayout(animate: false); };
        EffectiveViewportChanged += OnViewportChanged;
        Loaded += (_, _) => { HookRoot(); UpdateRunning(); };
        Unloaded += (_, _) => { UnhookRoot(); _tween?.Stop(); UpdateRunning(); };
    }

    /// <summary>The small version for the tray tile: one-line labels and only the main parts unless <see cref="Expandable"/>.</summary>
    public bool Compact { get; set; }

    /// <summary>Thickness of the river where it starts; every other stream is scaled from it.</summary>
    public double MainHeight { get; set; } = 200;

    /// <summary>Compact only: the parts can be opened in place (the tray tile then grows taller).</summary>
    public bool Expandable { get; set; }

    /// <summary>The slow light sweeping downstream; also needs <see cref="AnimationsEnabled"/>.</summary>
    public bool ShimmerEnabled { get; set; } = true;

    /// <summary>Compact only: names and watts next to the parts. Off leaves the river alone.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Off when Windows has animations turned off: no light sweep and no gliding.</summary>
    public bool AnimationsEnabled
    {
        get => _animations;
        set
        {
            if (_animations == value) return;
            _animations = value;
            UpdateRunning();
        }
    }

    /// <summary>The streams drawn now, in list order (a part before its own parts).</summary>
    public IReadOnlyList<FlowBox> Boxes => _layout.Boxes;

    public bool IsExpanded(string id) => _expanded.Contains(id);

    private bool CanOpen => !Compact || Expandable;

    /// <summary>Set by the page while it is shown; the light sweep only runs while it is.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (!active) _tween?.Stop();
        UpdateRunning();
    }

    /// <summary>Id of the single stream every part is blended into when <see cref="SetData"/> is called with <c>detailLocked</c>.</summary>
    public const string LockedPartsId = "pro.locked";

    /// <summary>
    /// Takes a new snapshot. <paramref name="mainsName"/> names a wall meter when one gives the total.
    /// <paramref name="detailLocked"/> blends every part (CPU, graphics, and the rest) into one unlabeled stream, so the
    /// total still flows through but no per-component number can be read off the diagram; used for non-Pro.
    /// </summary>
    public void SetData(PowerNode root, bool onBattery, bool isLaptop, string? mainsName = null, bool detailLocked = false)
    {
        _root = detailLocked ? Blend(root) : root;
        _onBattery = onBattery;
        _laptop = isLaptop;
        _mainsName = mainsName;
        RiverLayout.Prune(_expanded, _root);
        Relayout(animate: true);
    }

    private static PowerNode Blend(PowerNode root)
    {
        List<PowerNode> parts = root.Children
            .Where(c => c.Category is not (PowerCategory.PsuLoss or PowerCategory.Battery) && c.HasValue && c.Watts is double w && double.IsFinite(w) && w > 0)
            .ToList();
        if (parts.Count == 0) return root;
        List<PowerNode> children = [.. root.Children.Where(c => c.Category is PowerCategory.PsuLoss or PowerCategory.Battery)];
        children.Add(PowerNodes.Leaf(LockedPartsId, string.Empty, PowerCategory.Unaccounted, parts.Sum(c => c.Watts!.Value), PowerNodes.Combine(parts)));
        return root with { Children = children };
    }

    /// <summary>Opens or closes a part that has parts of its own.</summary>
    public void Toggle(string id)
    {
        if (!CanOpen || _layout.Find(id) is not { HasChildren: true }) return;
        FlowLayout.Toggle(_expanded, id);
        Relayout(animate: true);
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What selecting a stream does: the battery opens its details, a part with parts opens or closes.</summary>
    public void Activate(string id)
    {
        if (_layout.Find(id) is not { } b) return;
        if (b.IsBattery) BatteryRequested?.Invoke(this, EventArgs.Empty);
        else Toggle(id);
    }

    // ---- Names ----

    /// <summary>The words for a stream, in the user's language.</summary>
    public string Title(FlowBox b) => b.Role switch
    {
        FlowRole.Mains => _mainsName is { Length: > 0 } meter ? meter : Localization.Get(_laptop ? "PowerFlowCharger" : "PowerFlowMains"),
        FlowRole.Battery => Localization.Get("PowerFlowBattery"),
        FlowRole.Device => Localization.Get(_laptop ? "PowerFlowDeviceLaptop" : "PowerFlowDevicePc"),
        FlowRole.Loss => Localization.Get(_laptop ? "PowerFlowAdapterLoss" : "PowerFlowPsuLoss"),
        FlowRole.Charging => Localization.Get("PowerFlowChargingEndpoint"),
        FlowRole.Category when b.Node is { Id: LockedPartsId } => Localization.Get("PowerFlowLockedComponents"),
        FlowRole.Category when b.Node is { Id: RiverTile.OtherId } => Localization.Get("TrayPowerFlowOther"),
        FlowRole.Category => PowerPalette.NodeTitle(b.Node!, 0),
        _ => PowerPalette.NodeTitle(b.Node!, b.Level - 2),
    };

    private string Watts(FlowBox b)
    {
        if (b.Watts is not double w || !double.IsFinite(w)) return Localization.Get("PowerFlowNoValue");
        return Compact ? Localization.Format("PowerWattsWhole", w) : Localization.Format("PowerFlowWatts", w);
    }

    private bool Clickable(FlowBox b) => b.IsBattery || (CanOpen && b.HasChildren);

    private (string Tip, string Name) Describe(FlowBox b)
    {
        string title = Title(b);
        string watts = b.Watts is double w && double.IsFinite(w) ? Localization.Format("PowerFlowWatts", w) : Localization.Get("PowerFlowNoValue");
        string quality = PowerPalette.QualityName(b.Quality);
        string pct = b.Percent is double p ? Localization.Format("PowerSharePercent", p) : string.Empty;
        var tip = new List<string> { title, pct.Length > 0 ? $"{watts} · {pct} · {quality}" : $"{watts} · {quality}" };
        if (b.Node is { } n)
        {
            if (n.Name.Length > 0 && !string.Equals(n.Name, title, StringComparison.OrdinalIgnoreCase)) tip.Add(n.Name);
            if (!string.IsNullOrWhiteSpace(n.Detail)) tip.Add(n.Detail!);
        }
        bool opens = CanOpen && b.HasChildren;
        if (b.IsBattery) tip.Add(Localization.Get("PowerRiverTipBattery"));
        else if (opens) tip.Add(Localization.Get(b.Expanded ? "PowerRiverTipClose" : "PowerRiverTipOpen"));
        string state = opens ? ", " + Localization.Get(b.Expanded ? "PowerRowExpanded" : "PowerRowCollapsed") : string.Empty;
        return (string.Join("\n", tip), $"{title}, {watts}{(pct.Length > 0 ? ", " + pct : string.Empty)}, {quality}{state}");
    }

    // ---- Layout and gliding ----

    private FlowOptions Options(double width) => Compact
        ? new FlowOptions
        {
            Width = width, MainHeight = MainHeight, MinSlice = 1.5, Break = 2, RowHeight = 15, RowGap = 3, GroupGap = 5, Padding = 1,
            StubWidth = 11, LabelGap = 4, LabelWidth = ShowLabels ? Math.Clamp(width * 0.34, 76, 130) : 0, MinLabelWidth = 60,
            SourceWidth = 26, TrunkWidth = 30, SplitWidth = 26, MaxSourceWidth = 92, MaxTrunkWidth = 100,
            CapRadius = 9, LossDrop = 7, LossLabelHeight = 0, InsideLabelMin = 13, LabelLine = 13,
        }
        : new FlowOptions
        {
            Width = width, MainHeight = MainHeight, MinSlice = 2, Break = 3, RowHeight = 22, RowGap = 6, GroupGap = 12, Padding = 2,
            StubWidth = 22, LabelGap = 8, LabelWidth = width < 700 ? 112 : 140, MinLabelWidth = 84,
            SourceWidth = width < 700 ? 96 : 124, TrunkWidth = width < 700 ? 92 : 116, SplitWidth = width < 700 ? 72 : 96,
            MaxSourceWidth = 210, MaxTrunkWidth = 230, CapRadius = 16, LossDrop = 16, LossLabelHeight = 17, InsideLabelMin = 17, LabelLine = 16,
        };

    private void Relayout(bool animate)
    {
        double width = ActualWidth;
        if (width < 1) return;
        FlowOptions options = Options(Math.Max(width, Compact ? 160 : 360));
        _layout = FlowLayout.Compute(_root, _onBattery, _expanded, options, null, CanOpen ? int.MaxValue : 1);
        if (Compact && ShowLabels && options.LabelWidth > 0 && !_layout.IsEmpty)
        {
            // The name column is only as wide as the names shown, so the river takes the rest and reaches the right edge.
            // A part's watts count only where they do not fit inside its own stream (they then stand beside the name).
            double Need(bool withWatts)
            {
                double need = 0;
                foreach (FlowBox b in _layout.Boxes)
                {
                    if (b.Role is not (FlowRole.Category or FlowRole.Part or FlowRole.Charging)) continue;
                    string watts = Watts(b);
                    double wattsW = Measure(watts, 10, semiBold: false).Width;
                    bool beside = !withWatts ? false : _layout.Ribbons.FirstOrDefault(r => r.Kind == FlowSegmentKind.Split && r.ToId == b.Id) is not { } r || r.X1 - r.X0 < wattsW + 24;
                    double w = Measure(Title(b), 10.5, semiBold: true).Width + 17 + 4;
                    if (Clickable(b) && b.HasChildren) w += 14;
                    if (beside) w += wattsW + 4;
                    need = Math.Max(need, w);
                }
                return need;
            }
            double Narrow(double need) => Math.Clamp(need, 40, options.LabelWidth);
            double narrow = Narrow(Need(withWatts: false));
            if (narrow < options.LabelWidth - 0.5)
            {
                options = options with { LabelWidth = narrow, MinLabelWidth = Math.Min(options.MinLabelWidth, narrow) };
                _layout = FlowLayout.Compute(_root, _onBattery, _expanded, options, null, CanOpen ? int.MaxValue : 1);
                double again = Narrow(Need(withWatts: true));
                if (again > narrow + 0.5)
                {
                    options = options with { LabelWidth = again, MinLabelWidth = Math.Min(options.MinLabelWidth, again) };
                    _layout = FlowLayout.Compute(_root, _onBattery, _expanded, options, null, CanOpen ? int.MaxValue : 1);
                }
            }
        }
        bool empty = _layout.IsEmpty;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            _empty.Text = Localization.Get("PowerRiverEmpty");
            _empty.Foreground = ThemeBrushes.Get(this, "TextSecondaryBrush");
            _empty.FontSize = Compact ? 11.5 : 14;
            foreach (NodeView v in _nodes.Values) v.Detach(_nodeLayer);
            foreach (RibbonView v in _ribbons.Values) v.Detach(_ribbonLayer);
            _nodes.Clear();
            _ribbons.Clear();
            return;
        }
        _surface.Width = _layout.Width;

        FlowPalette palette = FlowPalette.Read(this) with { OnBattery = _onBattery };
        string paletteKey = palette.Key;
        bool restyle = paletteKey != _paletteKey;
        _paletteKey = paletteKey;
        UpdateShimmerBrush(palette);
        if (Waving && WaveKey() != _waveSpeedKey) RestartShimmer();

        bool glide = animate && _animations && _running && _nodes.Count > 0;
        // The river grows and shrinks with the parts shown, gliding to its new height along with them. The tray tile grows
        // at once and shrinks once the parts have gone, so its window never cuts the river off.
        double target = _layout.Height;
        if (!Compact) Height = double.NaN;
        if (_shownHeight < 0 || !glide) { _heightFrom = _heightTo = target; SetShownHeight(target); }
        else
        {
            if (Compact && target > _shownHeight) SetShownHeight(target);
            _heightFrom = _shownHeight;
            _heightTo = target;
        }
        double rootWatts = _root.Watts is double rw && double.IsFinite(rw) && rw > 0 ? rw : 1;
        var tips = new Dictionary<string, (string Tip, string Name)>();
        foreach (FlowBox b in _layout.Boxes) tips[b.Id] = Describe(b);

        // A part's watts go inside the stream splitting off towards it when the text fits there whole.
        var onRibbon = new HashSet<string>();
        var liveRibbons = new HashSet<string>();
        foreach (FlowRibbon r in _layout.Ribbons)
        {
            string key = r.Key;
            liveRibbons.Add(key);
            if (!_ribbons.TryGetValue(key, out RibbonView? view))
            {
                view = new RibbonView(r.Kind, r.ToId, _shimmer, Activate);
                _ribbons[key] = view;
                view.Attach(_ribbonLayer);
                RGeo end = RGeo.Of(r);
                view.Start = glide ? end with { X1 = end.X0 + 1, T1 = end.T0, B1 = end.B0, Opacity = 0 } : end;
                view.Drawn = view.Start;
            }
            else view.Start = view.Drawn;
            view.End = RGeo.Of(r);
            view.Leaving = false;
            FlowBox from = _layout.Find(r.FromId)!, to = _layout.Find(r.ToId)!;
            bool clickable = r.Kind is FlowSegmentKind.Split or FlowSegmentKind.Stub or FlowSegmentKind.Source && Clickable(to);
            view.Style(from, to, palette, restyle, clickable, tips[to.Id].Tip);
            // The waves shine brighter in the streams that carry more power.
            double share = r.Kind is FlowSegmentKind.Source or FlowSegmentKind.Trunk ? 1 : Math.Clamp((to.Watts ?? 0) / rootWatts, 0, 1);
            // Softer under the words on the source and the device, so they keep their contrast as a crest goes by.
            double under = r.Kind is FlowSegmentKind.Source or FlowSegmentKind.Trunk ? (Compact ? 0.5 : 0.7) : 1;
            view.SetGlow((0.4 + 0.6 * Math.Sqrt(share)) * under);
            string? label = r.Kind == FlowSegmentKind.Split && to.IsEndpoint && to.Watts is double lw && double.IsFinite(lw) ? Watts(to) : null;
            if (label is not null && view.Fits(label, palette, Compact, view.End)) onRibbon.Add(to.Id);
            else label = null;
            view.SetLabel(label, palette, Compact);
        }
        foreach ((string key, RibbonView view) in _ribbons)
            if (!liveRibbons.Contains(key) && !view.Leaving)
            {
                view.Leaving = true;
                view.Start = view.Drawn;
                view.End = view.Drawn with { X1 = view.Drawn.X0 + 1, T1 = view.Drawn.T0, B1 = view.Drawn.B0, Opacity = 0 };
            }

        var liveNodes = new HashSet<string>();
        foreach (FlowBox b in _layout.Boxes)
        {
            liveNodes.Add(b.Id);
            if (!_nodes.TryGetValue(b.Id, out NodeView? view))
            {
                view = new NodeView(b.Id, Compact, Activate);
                _nodes[b.Id] = view;
                view.Attach(_nodeLayer);
                view.Start = NGeo.Of(b) with { Opacity = glide ? 0 : 1 };
                view.Drawn = view.Start;
            }
            else view.Start = view.Drawn;
            view.End = NGeo.Of(b);
            view.Leaving = false;
            (string tip, string name) = tips[b.Id];
            view.Update(b, Title(b), Watts(b), tip, name, palette, restyle, _laptop, onRibbon.Contains(b.Id), Clickable(b), !Compact || ShowLabels);
        }
        foreach ((string id, NodeView view) in _nodes)
            if (!liveNodes.Contains(id) && !view.Leaving)
            {
                view.Leaving = true;
                view.Start = view.Drawn;
                view.End = view.Drawn with { Opacity = 0 };
            }

        if (!glide)
        {
            _tween?.Stop();
            Step(1);
            return;
        }
        _tweenStart = DateTime.UtcNow;
        if (_tween is null)
        {
            _tween = DispatcherQueue.CreateTimer();
            _tween.Interval = TimeSpan.FromMilliseconds(16);
            _tween.Tick += DispatcherCallbacks.GuardedTick((_, _) =>
            {
                double p = Math.Clamp((DateTime.UtcNow - _tweenStart) / TweenTime, 0, 1);
                Step(p);
                if (p >= 1) _tween?.Stop();
            });
        }
        if (!_tween.IsRunning) _tween.Start();
        Step(0);
    }

    private void Step(double p)
    {
        // Ease in and out, so a part opening glides out of its parent and settles softly.
        double e = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
        foreach (RibbonView v in _ribbons.Values) v.Draw(RGeo.Lerp(v.Start, v.End, e));
        foreach (NodeView v in _nodes.Values) v.Draw(NGeo.Lerp(v.Start, v.End, e));
        if (!Compact) SetShownHeight(L(_heightFrom, _heightTo, e));
        if (p < 1) return;
        SetShownHeight(_heightTo);
        foreach (string id in _nodes.Where(kv => kv.Value.Leaving).Select(kv => kv.Key).ToList())
        {
            _nodes[id].Detach(_nodeLayer);
            _nodes.Remove(id);
        }
        foreach (string key in _ribbons.Where(kv => kv.Value.Leaving).Select(kv => kv.Key).ToList())
        {
            _ribbons[key].Detach(_ribbonLayer);
            _ribbons.Remove(key);
        }
    }

    /// <summary>Raised when the tray river changes its height, so the window can follow.</summary>
    public event EventHandler? HeightChanged;

    private void SetShownHeight(double h)
    {
        if (Math.Abs(h - _shownHeight) < 0.01) return;
        _shownHeight = h;
        _surface.Height = h;
        if (!Compact) return;
        Height = h;
        HeightChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Warm waves of power flowing downstream ----

    /// <summary>
    /// One shared brush for every piece: soft warm crests repeating every <see cref="WavePeriod"/> pixels, slid downstream by
    /// a single endless animation of its transform, so the waves keep pulsing through the river without rebuilding a shape.
    /// Without motion it is a still, soft warm glow where the power comes in, fading downstream.
    /// </summary>
    private void UpdateShimmerBrush(FlowPalette p)
    {
        _wavePalette = p;
        ApplyLight();
    }

    private double WavePeriod => Compact ? 130 : 250;

    private bool Waving => _running && _animations && ShimmerEnabled && !_layout.IsEmpty;

    private void ApplyLight()
    {
        FlowPalette? p = _wavePalette;
        string lightKey = $"{p?.Key}|{Waving}|{Compact}|{(Waving ? 0 : Math.Round(_layout.Width))}";
        if (lightKey == _lightKey) return;
        _lightKey = lightKey;
        _shimmer.GradientStops.Clear();
        if (p is null || p.HighContrast) { _shimmer.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 255, 255, 255) }); return; }
        // Amber light: warm and bright on dark, deeper and quieter on light so it tints rather than washes out.
        Color warm = p.Dark ? Color.FromArgb(255, 0xFF, 0xD2, 0x6E) : Color.FromArgb(255, 0xF0, 0xA8, 0x2A);
        double peak = (p.Dark ? 0.24 : 0.22) * (Compact ? 0.85 : 1);
        Color At(double a) => FlowPalette.WithAlpha(warm, a);
        if (Waving)
        {
            _shimmer.SpreadMethod = GradientSpreadMethod.Repeat;
            _shimmer.EndPoint = new Point(WavePeriod, 0);
            // A gentle swell: a long soft rise to the crest and a longer tail behind it, never a hard stripe.
            (double Offset, double Alpha)[] crest = [(0, 0), (0.30, peak * 0.10), (0.52, peak * 0.62), (0.62, peak), (0.74, peak * 0.55), (0.90, peak * 0.12), (1, 0)];
            foreach ((double o, double a) in crest) _shimmer.GradientStops.Add(new GradientStop { Offset = o, Color = At(a) });
        }
        else
        {
            _shimmer.SpreadMethod = GradientSpreadMethod.Pad;
            _shimmer.EndPoint = new Point(Math.Max(1, _layout.Width), 0);
            _shimmerMove.X = 0;
            (double Offset, double Alpha)[] glow = [(0, peak * 0.55), (0.18, peak * 0.32), (0.45, peak * 0.08), (0.7, 0)];
            foreach ((double o, double a) in glow) _shimmer.GradientStops.Add(new GradientStop { Offset = o, Color = At(a) });
        }
    }

    private void RestartShimmer()
    {
        _shimmerBoard?.Stop();
        _shimmerBoard = null;
        ApplyLight();
        if (!Waving) return;
        // More power flows faster: from a calm drift at a few watts to a lively pulse at a few hundred.
        double total = _root.Watts is double w && double.IsFinite(w) ? w : 0;
        double share = Math.Clamp(Math.Log10(1 + total) / Math.Log10(301), 0, 1);
        double speed = (Compact ? 34 : 52) + share * (Compact ? 46 : 78);
        var drift = new DoubleAnimation
        {
            From = 0, To = WavePeriod, Duration = TimeSpan.FromSeconds(WavePeriod / speed),
            RepeatBehavior = RepeatBehavior.Forever, EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(drift, _shimmerMove);
        Storyboard.SetTargetProperty(drift, "X");
        _shimmerBoard = new Storyboard();
        _shimmerBoard.Children.Add(drift);
        _shimmerBoard.Begin();
        _waveSpeedKey = WaveKey();
    }

    private string WaveKey() => $"{Compact}|{Math.Round(Math.Log10(1 + (_root.Watts ?? 0)) * 10)}";

    // ---- Running or paused ----

    private void HookRoot()
    {
        if (XamlRoot is not { } root || ReferenceEquals(root, _hookedRoot)) return;
        UnhookRoot();
        _hookedRoot = root;
        root.Changed += OnRootChanged;
        _hostVisible = root.IsHostVisible;
    }

    private void UnhookRoot()
    {
        if (_hookedRoot is not null) _hookedRoot.Changed -= OnRootChanged;
        _hookedRoot = null;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_hostVisible == sender.IsHostVisible) return;
        _hostVisible = sender.IsHostVisible;
        UpdateRunning();
    }

    private void OnViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        Rect vp = args.EffectiveViewport;
        bool inView = vp.Width > 0 && vp.Height > 0 && vp.X < ActualWidth && vp.X + vp.Width > 0 && vp.Y < ActualHeight && vp.Y + vp.Height > 0;
        if (inView == _inView) return;
        _inView = inView;
        UpdateRunning();
    }

    private void UpdateRunning()
    {
        bool running = _active && _hostVisible && _inView && IsLoaded;
        bool changed = running != _running;
        _running = running;
        if (changed || (_shimmerBoard is null) == (running && _animations && ShimmerEnabled)) RestartShimmer();
    }

    // ---- Shapes ----

    /// <summary>Where a stream's hit area, its band and its name are.</summary>
    private readonly record struct NGeo(double X, double Y, double W, double H, double BandTop, double BandH, double LabelX, double LabelY, double LabelW, double Opacity)
    {
        public static NGeo Of(FlowBox b) => new(b.X, b.Y, b.Width, b.Height, b.FlowTop, b.FlowHeight, b.LabelX, b.LabelY, b.LabelWidth, 1);

        public static NGeo Lerp(NGeo a, NGeo b, double t) => new(
            L(a.X, b.X, t), L(a.Y, b.Y, t), L(a.W, b.W, t), L(a.H, b.H, t), L(a.BandTop, b.BandTop, t), L(a.BandH, b.BandH, t),
            L(a.LabelX, b.LabelX, t), L(a.LabelY, b.LabelY, t), L(a.LabelW, b.LabelW, t), L(a.Opacity, b.Opacity, t));
    }

    private static double L(double a, double b, double t) => a + (b - a) * t;

    private readonly record struct RGeo(double X0, double T0, double B0, double X1, double T1, double B1, double Cap0, double Cap1, double Opacity)
    {
        public static RGeo Of(FlowRibbon r) => new(r.X0, r.Top0, r.Bottom0, r.X1, r.Top1, r.Bottom1, r.CapStart, r.CapEnd, 1);

        public static RGeo Lerp(RGeo a, RGeo b, double t) => new(
            L(a.X0, b.X0, t), L(a.T0, b.T0, t), L(a.B0, b.B0, t), L(a.X1, b.X1, t), L(a.T1, b.T1, t), L(a.B1, b.B1, t),
            L(a.Cap0, b.Cap0, t), L(a.Cap1, b.Cap1, t), L(a.Opacity, b.Opacity, t));

        public FlowRibbon Ribbon(FlowSegmentKind kind) => new()
        {
            FromId = string.Empty, ToId = string.Empty, Kind = kind, X0 = X0, Top0 = T0, Bottom0 = B0, X1 = X1, Top1 = T1, Bottom1 = B1, CapStart = Cap0, CapEnd = Cap1,
        };
    }

    private static PathGeometry Geometry(IEnumerable<RiverPath> paths, bool filled)
    {
        var geometry = new PathGeometry();
        foreach (RiverPath path in paths)
        {
            var figure = new PathFigure { StartPoint = new Point(path.Start.X, path.Start.Y), IsClosed = path.Closed && filled, IsFilled = filled };
            foreach (RiverCubic c in path.Segments)
                figure.Segments.Add(new BezierSegment { Point1 = new Point(c.C1.X, c.C1.Y), Point2 = new Point(c.C2.X, c.C2.Y), Point3 = new Point(c.End.X, c.End.Y) });
            geometry.Figures.Add(figure);
        }
        return geometry;
    }

    /// <summary>The soft pull of both edges towards the middle: splits and the long straight stretches get it, short ends do not.</summary>
    private static double Waist(FlowSegmentKind kind, FlowRibbon r)
    {
        double len = Math.Abs(r.X1 - r.X0);
        return kind switch
        {
            FlowSegmentKind.Split => Math.Min(Math.Min(r.Thickness * 0.06, 12), len * 0.08),
            FlowSegmentKind.Source or FlowSegmentKind.Trunk => Math.Min(Math.Min(r.Thickness * 0.035, 5), len * 0.04),
            _ => 0,
        };
    }

    private static readonly TextBlock Meter = new() { TextWrapping = TextWrapping.NoWrap, TextLineBounds = TextLineBounds.Tight };

    /// <summary>The size of a line of text, measured on a text of its own: one that is not shown yet measures as nothing.</summary>
    private static (double Width, double Height) Measure(string text, double size, bool semiBold)
    {
        Meter.Text = text;
        Meter.FontSize = size;
        Meter.FontWeight = semiBold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        Meter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return (Math.Ceiling(Meter.DesiredSize.Width), Math.Ceiling(Meter.DesiredSize.Height));
    }

    private static Color Mix(Color a, Color b) => Color.FromArgb(255, (byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));

    /// <summary>One piece of the river: a faint halo, the gradient body, a soft rim, a light top edge and the shared sweep of light.</summary>
    private sealed class RibbonView
    {
        private readonly FlowSegmentKind _kind;
        private readonly Canvas _group = new();
        private readonly Path _halo = new() { IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };
        private readonly Path _body = new();
        private readonly Path _rim = new() { IsHitTestVisible = false, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
        private readonly Path _sheen = new() { IsHitTestVisible = false, StrokeThickness = 1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly Path _light = new() { IsHitTestVisible = false };
        private readonly TextBlock _label = new() { IsHitTestVisible = false, TextWrapping = TextWrapping.NoWrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextLineBounds = TextLineBounds.Tight, Visibility = Visibility.Collapsed };
        private string _styleKey = string.Empty, _tip = string.Empty;
        private bool _clickable, _hasLabel;
        public RGeo Start, End, Drawn;
        public bool Leaving;

        public RibbonView(FlowSegmentKind kind, string toId, Brush shimmer, Action<string> activate)
        {
            _kind = kind;
            // The fading loss would show its faded tail in the sweep of light, so it keeps out of it.
            if (kind != FlowSegmentKind.Loss) _light.Fill = shimmer;
            _group.Children.Add(_halo);
            _group.Children.Add(_body);
            _group.Children.Add(_light);
            _group.Children.Add(_rim);
            _group.Children.Add(_sheen);
            _group.Children.Add(_label);
            _body.Tapped += (_, e) => { if (_clickable) { activate(toId); e.Handled = true; } };
        }

        public void Attach(Canvas layer) => layer.Children.Add(_group);

        public void Detach(Canvas layer) => layer.Children.Remove(_group);

        /// <summary>How brightly the shared waves show in this piece.</summary>
        public void SetGlow(double strength)
        {
            _light.Opacity = strength;
            _light.Visibility = strength > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PrepareLabel(string text, FlowPalette p, bool compact)
        {
            _label.Text = text;
            _label.FontSize = compact ? 10 : 11.5;
            _label.Foreground = p.OnBandText;
        }

        /// <summary>True when <paramref name="text"/> fits whole inside this piece in its final shape.</summary>
        public bool Fits(string text, FlowPalette p, bool compact, RGeo g)
        {
            PrepareLabel(text, p, compact);
            return Place(g.Ribbon(_kind), out _, out _);
        }

        /// <summary>The watts written inside the stream, or null to leave them next to the part's name.</summary>
        public void SetLabel(string? text, FlowPalette p, bool compact)
        {
            _hasLabel = text is not null;
            if (text is null) { _label.Visibility = Visibility.Collapsed; return; }
            PrepareLabel(text, p, compact);
            PlaceLabel(Drawn.Ribbon(_kind));
        }

        /// <summary>
        /// Upright on the middle of the stream, two thirds of the way from the split towards the part (a little either side
        /// when the stream bends too steeply there), only where the text keeps clear of both edges.
        /// </summary>
        private bool Place(FlowRibbon r, out double left, out double top)
        {
            left = top = 0;
            (double w, double h) = Measure(_label.Text, _label.FontSize, semiBold: true);
            double len = r.X1 - r.X0;
            if (len < w + 12) return false;
            double waist = Waist(_kind, r);
            foreach (double t in new[] { LabelAlong, 0.72, 0.6, 0.78, 0.54 })
            {
                double cx = r.X0 + len * t;
                if (cx - w / 2 < r.X0 + 4 || cx + w / 2 > r.X1 - 4) continue;
                (double tc, double bc) = r.At(cx, 0.5, waist);
                double cy = (tc + bc) / 2;
                bool clear = true;
                foreach (double x in new[] { cx - w / 2 - 2, cx, cx + w / 2 + 2 })
                {
                    (double tt, double bb) = r.At(x, 0.5, waist);
                    if (tt > cy - h / 2 - 2 || bb < cy + h / 2 + 2) { clear = false; break; }
                }
                if (!clear) continue;
                left = Math.Round(cx - w / 2);
                top = Math.Round(cy - h / 2);
                return true;
            }
            return false;
        }

        private void PlaceLabel(FlowRibbon r)
        {
            if (!_hasLabel) return;
            bool fits = Place(r, out double left, out double top);
            _label.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
            if (!fits) return;
            Canvas.SetLeft(_label, left);
            Canvas.SetTop(_label, top);
        }

        public void Style(FlowBox from, FlowBox to, FlowPalette p, bool force, bool clickable, string tip)
        {
            _clickable = clickable;
            if (tip != _tip)
            {
                _tip = tip;
                ToolTipService.SetToolTip(_body, tip);
            }
            bool fades = _kind == FlowSegmentKind.Loss;
            string key = $"{p.Key}|{from.Role}|{to.Role}|{from.Node?.Category}|{to.Node?.Category}|{from.Index % 3}|{to.Index % 3}|{from.IsEstimate}|{to.IsEstimate}";
            if (!force && key == _styleKey) return;
            _styleKey = key;
            if (p.HighContrast)
            {
                _body.Fill = p.HcFill;
                _rim.Stroke = p.HcEdge;
                _halo.Stroke = null;
                _sheen.Stroke = null;
                return;
            }
            Color a = p.ColorFor(from), b = p.ColorFor(to);
            double a0 = p.RibbonAlpha * (from.IsEstimate ? p.EstimateStrength : 1);
            double a1 = fades ? 0 : p.RibbonAlpha * (to.IsEstimate ? p.EstimateStrength : 1);
            double mid = fades ? a0 * 0.45 : (a0 + a1) / 2 * (_kind == FlowSegmentKind.Split ? 0.9 : 1);
            var fill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            fill.GradientStops.Add(new GradientStop { Color = FlowPalette.WithAlpha(a, a0), Offset = 0 });
            fill.GradientStops.Add(new GradientStop { Color = FlowPalette.WithAlpha(Mix(a, b), mid), Offset = 0.5 });
            fill.GradientStops.Add(new GradientStop { Color = FlowPalette.WithAlpha(b, a1), Offset = 1 });
            _body.Fill = fill;
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            edge.GradientStops.Add(new GradientStop { Color = FlowPalette.WithAlpha(a, p.EdgeAlpha), Offset = 0 });
            edge.GradientStops.Add(new GradientStop { Color = FlowPalette.WithAlpha(b, fades ? 0 : p.EdgeAlpha), Offset = 1 });
            _rim.Stroke = edge;
            var halo = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            halo.GradientStops.Add(new GradientStop { Color = FlowPalette.WithAlpha(a, p.HaloAlpha), Offset = 0 });
            halo.GradientStops.Add(new GradientStop { Color = FlowPalette.WithAlpha(b, fades ? 0 : p.HaloAlpha), Offset = 1 });
            _halo.Stroke = halo;
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            sheen.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(p.SheenAlpha * 255), 255, 255, 255), Offset = 0 });
            sheen.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(fades ? 0 : p.SheenAlpha * 255), 255, 255, 255), Offset = 1 });
            _sheen.Stroke = sheen;
        }

        public void Draw(RGeo g)
        {
            Drawn = g;
            FlowRibbon r = g.Ribbon(_kind);
            double waist = Waist(_kind, r);
            RiverPath outline = r.Outline(0.5, waist);
            IReadOnlyList<RiverPath> rim = r.Rim(0.5, waist);
            _body.Data = Geometry([outline], filled: true);
            _light.Data = Geometry([outline], filled: true);
            _rim.Data = Geometry(rim, filled: false);
            _halo.Data = Geometry(rim, filled: false);
            _halo.StrokeThickness = Math.Clamp(r.Thickness * 0.14, 1.5, 5);
            _sheen.Data = Geometry([r.TopEdge(0.5, waist)], filled: false);
            _group.Opacity = g.Opacity;
            _sheen.Visibility = r.Thickness >= 5 ? Visibility.Visible : Visibility.Collapsed;
            PlaceLabel(r);
        }
    }

    /// <summary>
    /// The words and the hit area of one stream: the source's icon, name and watts inside its band; the PC or laptop symbol
    /// with the load under it; the loss under its fading stream; or a part's name (and its watts when they are not in its
    /// stream) next to its rounded end. A stream that does something when selected is a button.
    /// </summary>
    private sealed class NodeView
    {
        private readonly string _id;
        private readonly bool _compact;
        private readonly Grid _root = new();
        private readonly Border _plain = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        private readonly Button _button;
        // Inside a band: an icon and a name over the value.
        private readonly Grid _centerBox = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly StackPanel _center = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        private readonly StackPanel _centerLine = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6 };
        private readonly FontIcon _centerGlyph = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _centerTitle = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
        private readonly TextBlock _centerValue = new() { TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
        // Next to a rounded end: an icon, the name, the watts and a chevron on one line.
        private readonly Grid _line = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly FontIcon _lineGlyph = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _lineName = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _lineWatts = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly FontIcon _chevron = new() { VerticalAlignment = VerticalAlignment.Center };
        private string _styleKey = string.Empty, _tip = string.Empty;
        private FlowRole _role;
        private bool _inside, _labels, _wattsBeside, _clickable;
        private double _lineExtra, _lineHeight, _centerValueW, _centerTitleW;
        public NGeo Start, End, Drawn;
        public bool Leaving;

        public NodeView(string id, bool compact, Action<string> activate)
        {
            _id = id;
            _compact = compact;
            _centerLine.Children.Add(_centerGlyph);
            _centerLine.Children.Add(_centerTitle);
            _center.Children.Add(_centerLine);
            _center.Children.Add(_centerValue);
            _centerBox.Children.Add(_center);
            _line.ColumnSpacing = compact ? 4 : 6;
            for (int i = 0; i < 4; i++) _line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_lineName, 1);
            Grid.SetColumn(_lineWatts, 2);
            Grid.SetColumn(_chevron, 3);
            _line.Children.Add(_lineGlyph);
            _line.Children.Add(_lineName);
            _line.Children.Add(_lineWatts);
            _line.Children.Add(_chevron);
            _button = new Button
            {
                Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(compact ? 5 : 8),
                Visibility = Visibility.Collapsed,
            };
            _button.Click += (_, _) => activate(_id);
            _root.Children.Add(_plain);
            _root.Children.Add(_button);
            _root.Children.Add(_centerBox);
            _root.Children.Add(_line);
        }

        public void Attach(Canvas layer) => layer.Children.Add(_root);

        public void Detach(Canvas layer) => layer.Children.Remove(_root);

        public void Update(FlowBox b, string title, string watts, string tip, string name, FlowPalette p, bool force, bool laptop, bool wattsOnRibbon, bool clickable, bool labels)
        {
            _role = b.Role;
            _clickable = clickable;
            _labels = labels;
            bool center = b.Role is FlowRole.Mains or FlowRole.Battery or FlowRole.Device;
            // An open part writes its name inside its own stream when the stream is thick enough.
            _inside = center || (b.Expanded && !b.LabelAbove && b.IsEndpoint);
            _centerBox.Visibility = center ? Visibility.Visible : Visibility.Collapsed;
            bool line = !center && labels && !(_compact && b.Role == FlowRole.Loss);
            _line.Visibility = line ? Visibility.Visible : Visibility.Collapsed;

            string glyph = b.Role switch
            {
                FlowRole.Mains => "",
                FlowRole.Battery => "",
                FlowRole.Charging => "",
                FlowRole.Device => laptop ? "" : "",
                _ => string.Empty,
            };
            if (center)
            {
                bool device = b.Role == FlowRole.Device;
                _centerGlyph.Glyph = glyph;
                _centerGlyph.FontSize = _compact ? (device ? 14 : 11) : device ? 22 : 14;
                _centerTitle.Text = device ? string.Empty : title;
                _centerTitle.FontSize = _compact ? 10.5 : 12.5;
                _centerTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                _centerTitle.Visibility = device ? Visibility.Collapsed : Visibility.Visible;
                _centerValue.Text = watts;
                _centerValue.FontSize = _compact ? 11 : device ? 19 : 17;
                _centerValue.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                _centerValue.FontFamily = (FontFamily)Application.Current.Resources["DisplayFontFamily"];
                _center.Spacing = _compact ? 3 : device ? 7 : 6;
                _centerValueW = Measure(watts, _centerValue.FontSize, semiBold: true).Width;
                _centerTitleW = device ? 0 : Measure(title, _centerTitle.FontSize, semiBold: true).Width + _centerGlyph.FontSize * 1.25 + _centerLine.Spacing;
            }
            else
            {
                _lineGlyph.Glyph = glyph;
                _lineGlyph.FontSize = _compact ? 10 : 12;
                _lineGlyph.Visibility = glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _lineName.Text = title;
                _lineName.FontSize = _compact ? 10.5 : 12.5;
                _lineName.FontWeight = b.Role == FlowRole.Loss ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
                _wattsBeside = !wattsOnRibbon;
                _lineWatts.Text = watts;
                _lineWatts.FontSize = _compact ? 10 : 11.5;
                _lineWatts.Visibility = _wattsBeside ? Visibility.Visible : Visibility.Collapsed;
                bool chevron = clickable && b.HasChildren;
                _chevron.Visibility = chevron ? Visibility.Visible : Visibility.Collapsed;
                _chevron.Glyph = b.Expanded ? "" : "";
                _chevron.FontSize = _compact ? 7 : 8;
                _line.HorizontalAlignment = b.Role == FlowRole.Loss ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                _lineHeight = _compact ? 13 : 16;
                _line.Height = _lineHeight;
                // Room the name leaves for the rest of its line.
                double extra = 0;
                if (_lineGlyph.Visibility == Visibility.Visible) extra += _lineGlyph.FontSize * 1.25 + _line.ColumnSpacing;
                if (_wattsBeside) extra += Measure(watts, _lineWatts.FontSize, semiBold: false).Width + _line.ColumnSpacing;
                if (chevron) extra += _chevron.FontSize * 1.4 + _line.ColumnSpacing;
                _lineExtra = extra;
            }

            _button.Visibility = clickable ? Visibility.Visible : Visibility.Collapsed;
            _button.IsTabStop = clickable;
            _plain.Visibility = clickable ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(_button, name);
            AutomationProperties.SetName(_plain, name);
            if (tip != _tip)
            {
                _tip = tip;
                ToolTipService.SetToolTip(_root, tip);
            }

            string key = $"{p.Key}|{b.Role}|{_inside}|{_wattsBeside}";
            if (!force && key == _styleKey) return;
            _styleKey = key;
            Brush onBand = p.OnBandText;
            _centerGlyph.Foreground = onBand;
            _centerTitle.Foreground = onBand;
            _centerValue.Foreground = onBand;
            bool loss = b.Role == FlowRole.Loss;
            _lineName.Foreground = _inside ? onBand : loss ? p.SecondaryText : p.PrimaryText;
            _lineWatts.Foreground = _inside ? onBand : p.SecondaryText;
            _lineWatts.Opacity = _inside ? 0.8 : 1;
            _chevron.Foreground = _inside ? onBand : p.SecondaryText;
            _lineGlyph.Foreground = p.HighContrast ? p.PrimaryText : new SolidColorBrush(p.GlyphColor(b));
        }

        public void Draw(NGeo g)
        {
            Drawn = g;
            // Whole pixels keep the text crisp.
            g = g with { X = Math.Round(g.X), Y = Math.Round(g.Y) };
            Canvas.SetLeft(_root, g.X);
            Canvas.SetTop(_root, g.Y);
            bool endpoint = _role is FlowRole.Category or FlowRole.Part or FlowRole.Charging;
            // A part's hit area reaches over its name too; the loss is only its name.
            double right = endpoint && _labels ? Math.Max(g.X + g.W, g.LabelX + g.LabelW) : g.X + g.W;
            _root.Width = Math.Max(1, Math.Round(right - g.X));
            _root.Height = Math.Max(1, Math.Round(g.H));
            _root.Opacity = g.Opacity;
            if (_centerBox.Visibility == Visibility.Visible)
            {
                // Centred in the band, clear of the rounded start of a source.
                double inset = _role == FlowRole.Device ? 0 : _compact ? 4 : 8;
                _centerBox.Margin = new Thickness(inset, Math.Round(g.BandTop - g.Y), 0, 0);
                _centerBox.Width = Math.Max(1, Math.Round(g.W - inset));
                _centerBox.Height = Math.Max(1, Math.Round(g.BandH));
                // A thin band keeps the value only, next to the icon.
                bool two = g.BandH >= (_compact ? 28 : 50) && g.W >= (_compact ? 40 : 64);
                double room = g.W - inset - (_compact ? 4 : 10);
                bool title = _role != FlowRole.Device && two && room >= _centerTitleW;
                _centerTitle.Visibility = title ? Visibility.Visible : Visibility.Collapsed;
                _centerGlyph.Visibility = g.BandH >= (_compact ? 12 : 18) ? Visibility.Visible : Visibility.Collapsed;
                _center.Orientation = two ? Orientation.Vertical : Orientation.Horizontal;
                // The value shows whole or not at all; the tile's title row and the tooltip still carry it.
                bool valueFits = two ? room >= _centerValueW : room >= _centerValueW + _centerGlyph.FontSize * 1.25 + _center.Spacing;
                _centerValue.Visibility = g.BandH >= (_compact ? 9 : 12) && valueFits ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_line.Visibility == Visibility.Visible)
            {
                _lineName.MaxWidth = Math.Max(8, g.LabelW - _lineExtra);
                double top = Math.Round(g.LabelY - g.Y - _lineHeight / 2);
                // The loss's name sits under the end of its fading stream, flush right.
                if (_role == FlowRole.Loss) { _line.Width = double.NaN; _line.Margin = new Thickness(0, top, 0, 0); }
                else { _line.Width = Math.Max(1, g.LabelW); _line.Margin = new Thickness(Math.Round(g.LabelX - g.X), top, 0, 0); }
            }
        }
    }
}
