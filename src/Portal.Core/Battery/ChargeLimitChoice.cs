namespace Portal.Core.Battery;

/// <summary>The limit value used when the limit is switched on without the user picking one. Pure.</summary>
public static class ChargeLimitChoice
{
    public static int? PreferredValue(ChargeLimitCapabilities caps, ChargeLimitState state, ChargeIntent? intent)
    {
        int preferred = intent is { LimitOn: true } ? intent.Limit : state.Limit is int l && l < 100 ? l : 80;
        switch (caps.Kind)
        {
            case ChargeLimitKind.ContinuousRange:
                return Math.Clamp(preferred, caps.MinLimit, caps.MaxLimit);
            case ChargeLimitKind.FixedValues:
                var values = caps.DiscreteValues.Where(v => v < 100).ToList();
                return values.Contains(preferred) ? preferred : values.Count > 0 ? values[^1] : null;
            default:
                return null;
        }
    }
}
