namespace JriksSoundboard.Core.Audio;

/// <summary>
/// Keeps decoded sounds in memory (least-recently-used first out) within a byte budget.
/// Sounds on wheels are preloaded so they start instantly; anything else is decoded on
/// first play (a few milliseconds for a short clip).
/// </summary>
public sealed class SoundCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> lru = new();
    private readonly Func<string, CachedSound> loader;
    private long usedBytes;
    private long evictions;

    public SoundCache(long budgetBytes = 256L * 1024 * 1024, Func<string, CachedSound>? loader = null)
    {
        BudgetBytes = budgetBytes;
        this.loader = loader ?? CachedSound.Load;
    }

    public long BudgetBytes { get; }

    public long UsedBytes { get { lock (gate) return usedBytes; } }

    public int Count { get { lock (gate) return entries.Count; } }

    /// <summary>Returns the decoded sound, loading it if needed. Thread-safe. Throws if the file can't be decoded.</summary>
    public CachedSound Get(string path)
    {
        string key = KeyFor(path);
        lock (gate)
        {
            if (entries.TryGetValue(key, out LinkedListNode<Entry>? hit))
            {
                Touch(hit);
                return hit.Value.Sound;
            }
        }

        // Decode outside the lock so one slow file doesn't block other threads.
        CachedSound sound = loader(path);

        lock (gate)
        {
            if (entries.TryGetValue(key, out LinkedListNode<Entry>? raced))
            {
                Touch(raced);
                return raced.Value.Sound;
            }
            entries[key] = lru.AddFirst(new Entry(key, sound));
            usedBytes += SizeOf(sound);
            EvictLocked();
        }
        return sound;
    }

    public bool IsCached(string path)
    {
        string key = KeyFor(path);
        lock (gate) return entries.ContainsKey(key);
    }

    /// <summary>
    /// Loads sounds until memory is full (the first eviction), so preloading never pushes out
    /// sounds it just loaded. Returns the files that failed to load.
    /// </summary>
    public IReadOnlyList<(string Path, Exception Error)> Preload(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var failures = new List<(string, Exception)>();
        foreach (string path in paths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            long evictionsBefore = Interlocked.Read(ref evictions);
            try
            {
                Get(path);
            }
            catch (Exception ex)
            {
                failures.Add((path, ex));
            }
            if (Interlocked.Read(ref evictions) > evictionsBefore)
            {
                break; // budget reached
            }
        }
        return failures;
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        lru.Remove(node);
        lru.AddFirst(node);
    }

    private void EvictLocked()
    {
        while (usedBytes > BudgetBytes && lru.Count > 1)
        {
            LinkedListNode<Entry> oldest = lru.Last!;
            lru.RemoveLast();
            entries.Remove(oldest.Value.Key);
            usedBytes -= SizeOf(oldest.Value.Sound);
            Interlocked.Increment(ref evictions);
        }
    }

    private static long SizeOf(CachedSound sound) => sound.Samples.LongLength * sizeof(float);

    // Includes the file's last-write time so an edited file is decoded again.
    private static string KeyFor(string path)
    {
        string full = Path.GetFullPath(path);
        long ticks = File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : 0;
        return $"{full}|{ticks}";
    }

    private sealed record Entry(string Key, CachedSound Sound);
}
