using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Portal.Core.Battery;

namespace BatteryCarePortal;

/// <summary>What's new: 3-4 highlight cards from the release's embedded JSON, each with a Show me link, one Close button, Esc closes.</summary>
internal sealed class WhatsNewWindow : Window
{
    private const int WidthDip = 520;
    private const int HeightDip = 600;

    private WhatsNewWindow(Window owner, WhatsNewRelease release)
    {
        AppIcon.Apply(this);
        Title = Strings.Get("WhatsNewTitle");
        var cards = new StackPanel { Spacing = 8 };
        foreach (WhatsNewCard card in release.Cards) cards.Children.Add(BuildCard(card));

        var close = new Button { Content = Strings.Get("WhatsNewClose"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var layout = new Grid { Padding = new Thickness(24), RowSpacing = 12 };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        layout.Children.Add(new TextBlock
        {
            Text = string.Format(Strings.Get("WhatsNewHeading"), release.Version),
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        var scroll = new ScrollViewer { Content = cards };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(close, 2);
        layout.Children.Add(close);

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(); };
        layout.KeyboardAccelerators.Add(escape);

        Content = layout;
        AppAppearance.AttachExtra(this, layout);
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsMaximizable = false; presenter.IsMinimizable = false; }
        WindowPlacement.CenterOn(this, owner, WidthDip, HeightDip);
    }

    private static Border BuildCard(WhatsNewCard card)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = card.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = card.Text, TextWrapping = TextWrapping.Wrap });
        if (card.Page.Length > 0)
        {
            var show = new HyperlinkButton { Content = Strings.Get("WhatsNewShowMe"), Padding = new Thickness(0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(show, string.Format(Strings.Get("WhatsNewShowMeNamed"), card.Title));
            show.Click += (_, _) => App.Instance?.OpenPage(card.Page);
            body.Children.Add(show);
        }
        return new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = (CornerRadius)Application.Current.Resources["BcpCardRadius"],
            BorderThickness = new Thickness(1),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = body,
        };
    }

    /// <summary>The release highlights for a version (major.minor.build), read from the embedded file; null when there is none.</summary>
    public static WhatsNewRelease? Load(string version)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"WhatsNew.{version}.json");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return WhatsNewLogic.Parse(reader.ReadToEnd());
        }
        catch (Exception) { return null; }
    }

    /// <summary>Opens the window for this version. False when the release has no highlights file.</summary>
    public static bool TryShow(Window owner, string version)
    {
        if (Load(version) is not { } release) return false;
        new WhatsNewWindow(owner, release).Activate();
        return true;
    }
}
