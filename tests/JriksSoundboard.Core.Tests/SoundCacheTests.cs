using JriksSoundboard.Core.Audio;

namespace JriksSoundboard.Core.Tests;

public class SoundCacheTests
{
    private readonly List<string> loads = [];

    private CachedSound FakeLoad(string path)
    {
        loads.Add(path);
        if (path.Contains("bad"))
        {
            throw new InvalidDataException("not audio");
        }
        return CachedSound.CreateTestTone(seconds: 1.0); // 48000 × 2 floats ≈ 375 KB
    }

    [Fact]
    public void Second_get_is_served_from_memory()
    {
        var cache = new SoundCache(10_000_000, FakeLoad);

        CachedSound first = cache.Get("a.wav");
        CachedSound second = cache.Get("a.wav");

        Assert.Same(first, second);
        Assert.Single(loads);
        Assert.True(cache.IsCached("a.wav"));
    }

    [Fact]
    public void Least_recently_used_sound_is_evicted_when_over_budget()
    {
        var cache = new SoundCache(500_000, FakeLoad); // room for one tone only

        cache.Get("a.wav");
        cache.Get("b.wav");

        Assert.Equal(1, cache.Count);
        Assert.False(cache.IsCached("a.wav"));
        Assert.True(cache.IsCached("b.wav"));
        Assert.True(cache.UsedBytes <= cache.BudgetBytes);

        cache.Get("a.wav"); // decoded again
        Assert.Equal(3, loads.Count);
    }

    [Fact]
    public void Preload_reports_failures_and_keeps_going()
    {
        var cache = new SoundCache(10_000_000, FakeLoad);

        var failures = cache.Preload(["a.wav", "bad.wav", "c.wav"]);

        var failure = Assert.Single(failures);
        Assert.Equal("bad.wav", failure.Path);
        Assert.True(cache.IsCached("a.wav"));
        Assert.True(cache.IsCached("c.wav"));
    }

    [Fact]
    public void Preload_stops_once_the_budget_is_full()
    {
        var cache = new SoundCache(500_000, FakeLoad);

        cache.Preload(["a.wav", "b.wav", "c.wav"]);

        Assert.Equal(2, loads.Count); // after the 2nd tone the budget is full (the 1st was evicted to fit)
    }
}
