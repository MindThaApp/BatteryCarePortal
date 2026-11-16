using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Portal.Core.UI.Controls;

/// <summary>
/// Small, springy entrance motion for pages: a short fade with a slide up that settles with a slight overshoot.
/// Runs on the composition layer (no layout work) and is skipped when Windows has animation effects turned off.
/// </summary>
internal static class Motion
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(340);
    private const float SlideDistance = 10f;

    public static bool AnimationsEnabled
    {
        get
        {
            // A safe start (after a crash loop) runs no optional animation.
            if (UiSafeMode.Enabled) return false;
            try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch { return true; }
        }
    }

    /// <summary>Plays the entrance on a page that has just been shown.</summary>
    public static void PageIn(UIElement page)
    {
        if (!AnimationsEnabled) return;
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(page);
            Compositor compositor = visual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(page, true);

            CompositionEasingFunction settle = SpringOut(compositor);
            CompositionEasingFunction fadeEase = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, fadeEase);
            fade.Duration = FadeDuration;

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(0f, SlideDistance, 0f));
            slide.InsertKeyFrame(1f, Vector3.Zero, settle);
            slide.Duration = SlideDuration;

            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Translation", slide);
        }
        catch
        {
            // Motion is decoration only; the page is already visible without it.
        }
    }

    // A cubic curve whose second control point rises just above 1 overshoots by a few percent and settles back,
    // which reads as a light spring; a plain decelerating curve if the overshoot curve is refused.
    private static CompositionEasingFunction SpringOut(Compositor compositor)
    {
        try { return compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1.08f)); }
        catch { return compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)); }
    }
}
