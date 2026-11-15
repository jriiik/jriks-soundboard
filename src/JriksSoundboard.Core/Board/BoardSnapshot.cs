namespace JriksSoundboard.Core.Board;

public sealed record SoundSnapshot(Guid Id, string Name, string FilePath, float Volume);

public sealed record SlotSnapshot(SlotKind Kind, Guid? SoundId, string Label, PageSnapshot? Folder)
{
    public static readonly SlotSnapshot Empty = new(SlotKind.Empty, null, string.Empty, null);
}

public sealed record PageSnapshot(IReadOnlyList<SlotSnapshot> Slots);

public sealed record WheelSnapshot(Guid Id, string Name, int Hotkey, PageSnapshot Root);

/// <summary>
/// Immutable copy of the board for the input/audio threads. The UI edits the observable
/// <see cref="SoundBoard"/>; after each edit a fresh snapshot is swapped in, so the hotkey
/// thread never reads a collection while the UI is changing it.
/// </summary>
public sealed class BoardSnapshot
{
    public static readonly BoardSnapshot Empty = new([], new Dictionary<Guid, SoundSnapshot>());

    private BoardSnapshot(IReadOnlyList<WheelSnapshot> wheels, IReadOnlyDictionary<Guid, SoundSnapshot> sounds)
    {
        Wheels = wheels;
        Sounds = sounds;
    }

    public IReadOnlyList<WheelSnapshot> Wheels { get; }

    public IReadOnlyDictionary<Guid, SoundSnapshot> Sounds { get; }

    public WheelSnapshot? WheelForKey(int virtualKey) =>
        virtualKey == 0 ? null : Wheels.FirstOrDefault(w => w.Hotkey == virtualKey);

    public static BoardSnapshot From(SoundBoard board)
    {
        var sounds = new Dictionary<Guid, SoundSnapshot>();
        foreach (SoundItem s in board.Sounds)
        {
            sounds.TryAdd(s.Id, new SoundSnapshot(s.Id, s.Name, s.FilePath, s.Volume));
        }

        var wheels = board.Wheels
            .Select(w => new WheelSnapshot(w.Id, w.Name, w.Hotkey, Page(w.Root, sounds)))
            .ToList();
        return new BoardSnapshot(wheels, sounds);
    }

    /// <summary>All sound ids reachable from a page (including inside folders).</summary>
    public static IEnumerable<Guid> SoundIdsIn(PageSnapshot page)
    {
        foreach (SlotSnapshot slot in page.Slots)
        {
            if (slot.Kind == SlotKind.Sound && slot.SoundId is Guid id)
            {
                yield return id;
            }
            else if (slot.Folder is { } folder)
            {
                foreach (Guid inner in SoundIdsIn(folder))
                {
                    yield return inner;
                }
            }
        }
    }

    private static PageSnapshot Page(WheelPage page, IReadOnlyDictionary<Guid, SoundSnapshot> sounds) =>
        new(page.Slots.Select(slot => Slot(slot, sounds)).ToList());

    private static SlotSnapshot Slot(WheelSlot slot, IReadOnlyDictionary<Guid, SoundSnapshot> sounds)
    {
        if (slot.Kind == SlotKind.Sound && slot.SoundId is Guid id && sounds.TryGetValue(id, out SoundSnapshot? sound))
        {
            return new SlotSnapshot(SlotKind.Sound, id, slot.Label ?? sound.Name, null);
        }
        if (slot.Kind == SlotKind.Folder && slot.Folder is not null)
        {
            return new SlotSnapshot(SlotKind.Folder, null, slot.Label ?? "Folder", Page(slot.Folder, sounds));
        }
        return SlotSnapshot.Empty;
    }
}
