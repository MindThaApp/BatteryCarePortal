using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

public class LargeValueStoreTests
{
    private const string Key = LargeValueStore.FanControlKey;

    [Theory]
    [InlineData(0)]
    [InlineData(2999)]
    [InlineData(3000)]
    [InlineData(3001)]
    [InlineData(9000)]
    [InlineData(50000)]
    public void RoundTripsAndKeepsEveryValueUnderTheLimit(int length)
    {
        var values = new Dictionary<string, object>();
        string text = new string('x', length);
        if (length > 0) text = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));
        LargeValueStore.Set(values, Key, text);
        Assert.Equal(text, LargeValueStore.Get(values, Key));
        Assert.All(values.Values.OfType<string>(), s => Assert.True(s.Length <= LargeValueStore.Chunk));
    }

    [Fact]
    public void ShrinkingRemovesStaleParts()
    {
        var values = new Dictionary<string, object>();
        LargeValueStore.Set(values, Key, new string('a', 50000));
        Assert.True(values.Count > 10);
        LargeValueStore.Set(values, Key, new string('b', 7000));
        Assert.Equal(new[] { Key, Key + "#1", Key + "#2", Key + "#n" }.OrderBy(k => k), values.Keys.OrderBy(k => k));
        LargeValueStore.Set(values, Key, "small");
        Assert.Equal(new[] { Key }, values.Keys);
        Assert.Equal("small", LargeValueStore.Get(values, Key));
    }

    [Fact]
    public void MissingPartReadsAsNull()
    {
        var values = new Dictionary<string, object>();
        LargeValueStore.Set(values, Key, new string('a', 9000));
        values.Remove(Key + "#1");
        Assert.Null(LargeValueStore.Get(values, Key));
    }

    [Fact]
    public void PartKeysAreRecognisedForProfileExport()
    {
        Assert.True(LargeValueStore.IsPartKey(Key + "#1"));
        Assert.True(LargeValueStore.IsPartKey(Key + "#n"));
        Assert.False(LargeValueStore.IsPartKey(Key));
    }
}
