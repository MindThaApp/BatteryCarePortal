namespace Portal.Core.Services;

/// <summary>Stores a long string across several settings values (one value holds at most 8 KB).</summary>
public static class LargeValueStore
{
    public const int Chunk = 3000;
    public const string FanControlKey = "FanControlSettings";

    public static bool IsPartKey(string key) => key.Contains('#');

    public static string? Get(IDictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out object? v) || v is not string first) return null;
        if (!values.TryGetValue(key + "#n", out object? n) || n is not int count || count <= 1) return first;
        var sb = new System.Text.StringBuilder(first, count * Chunk);
        for (int i = 1; i < count; i++)
        {
            if (!values.TryGetValue($"{key}#{i}", out object? part) || part is not string s) return null;
            sb.Append(s);
        }
        return sb.ToString();
    }

    public static void Set(IDictionary<string, object> values, string key, string? value)
    {
        value ??= string.Empty;
        int count = Math.Max(1, (value.Length + Chunk - 1) / Chunk);
        int old = values.TryGetValue(key + "#n", out object? n) && n is int c ? c : 1;
        // Tail parts first, then the count, then the head: a reader never sees a count without its parts.
        for (int i = 1; i < count; i++) values[$"{key}#{i}"] = value.Substring(i * Chunk, Math.Min(Chunk, value.Length - i * Chunk));
        if (count > 1) values[key + "#n"] = count; else values.Remove(key + "#n");
        values[key] = value.Length <= Chunk ? value : value[..Chunk];
        for (int i = count; i < old; i++) values.Remove($"{key}#{i}");
    }
}
