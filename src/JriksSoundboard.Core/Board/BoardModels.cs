using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.Core.Board;

/// <summary>Minimal INotifyPropertyChanged base so the UI can bind straight to the board.</summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One audio file in the library. Files are referenced where they are, not copied.</summary>
public sealed class SoundItem : ObservableModel
{
    private string name = string.Empty;
    private string filePath = string.Empty;
    private float volume = 1f;
    private double durationSeconds;
    private bool isMissing;
    private bool isOnWheel;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get => name; set => Set(ref name, value ?? string.Empty); }

    public string FilePath { get => filePath; set => Set(ref filePath, value ?? string.Empty); }

    /// <summary>Per-sound gain on top of the global volume (0–2, 1 = unchanged).</summary>
    public float Volume { get => volume; set => Set(ref volume, Math.Clamp(value, 0f, 2f)); }

    public double DurationSeconds
    {
        get => durationSeconds;
        set
        {
            if (Set(ref durationSeconds, value))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    [JsonIgnore]
    public string DurationText => DurationSeconds switch
    {
        <= 0.0 => string.Empty,
        < 60.0 => $"{DurationSeconds:0.0}s",
        _ => $"{(int)DurationSeconds / 60}:{(int)DurationSeconds % 60:00}",
    };

    [JsonIgnore]
    public bool IsMissing { get => isMissing; private set => Set(ref isMissing, value); }

    /// <summary>True when at least one wheel slot points at this sound (kept up to date by the board).</summary>
    [JsonIgnore]
    public bool IsOnWheel { get => isOnWheel; set => Set(ref isOnWheel, value); }

    public void RefreshFileState() => IsMissing = !File.Exists(FilePath);
}

/// <summary>One slice of a wheel: empty, a sound, or a folder (a sub-wheel, one level deep).</summary>
public sealed class WheelSlot : ObservableModel
{
    private SlotKind kind;
    private Guid? soundId;
    private string? label;
    private WheelPage? folder;

    public SlotKind Kind { get => kind; set => Set(ref kind, value); }

    public Guid? SoundId { get => soundId; set => Set(ref soundId, value); }

    /// <summary>Folder name, or an optional custom label for a sound slot.</summary>
    public string? Label { get => label; set => Set(ref label, value); }

    public WheelPage? Folder { get => folder; set => Set(ref folder, value); }

    [JsonIgnore]
    public bool IsEmpty => Kind == SlotKind.Empty;

    public void AssignSound(Guid id)
    {
        Folder = null;
        Label = null;
        SoundId = id;
        Kind = SlotKind.Sound;
    }

    public void MakeFolder(string folderName, int sliceCount = 8)
    {
        SoundId = null;
        Label = folderName;
        Folder = WheelPage.Create(sliceCount);
        Kind = SlotKind.Folder;
    }

    public void Clear()
    {
        SoundId = null;
        Label = null;
        Folder = null;
        Kind = SlotKind.Empty;
    }

    public void CopyFrom(WheelSlot other)
    {
        SoundId = other.SoundId;
        Label = other.Label;
        Folder = other.Folder;
        Kind = other.Kind;
    }

    public static void Swap(WheelSlot a, WheelSlot b)
    {
        var temp = new WheelSlot();
        temp.CopyFrom(a);
        a.CopyFrom(b);
        b.CopyFrom(temp);
    }
}

/// <summary>The slices of one wheel (or of a folder). Index 0 is straight up, then clockwise.</summary>
public sealed class WheelPage
{
    public static readonly int[] AllowedSliceCounts = [4, 6, 8, 10, 12, 16];

    public ObservableCollection<WheelSlot> Slots { get; set; } = [];

    [JsonIgnore]
    public int SliceCount => Slots.Count;

    public static WheelPage Create(int sliceCount)
    {
        ValidateCount(sliceCount);
        var page = new WheelPage();
        for (int i = 0; i < sliceCount; i++)
        {
            page.Slots.Add(new WheelSlot());
        }
        return page;
    }

    /// <summary>How many filled slots would not fit if the wheel were resized (for a confirmation prompt).</summary>
    public int CountSlotsLostByResize(int newCount) => PlanResize(newCount).Lost;

    /// <summary>
    /// Changes the number of slices while keeping each sound pointing in (roughly) the same
    /// direction, so muscle memory survives: a sound that was "right" stays "right".
    /// </summary>
    public void Resize(int newCount)
    {
        ValidateCount(newCount);
        (List<(WheelSlot Slot, int Index)> placed, _) = PlanResize(newCount);

        var fresh = new WheelSlot[newCount];
        for (int i = 0; i < newCount; i++)
        {
            fresh[i] = new WheelSlot();
        }
        foreach ((WheelSlot slot, int index) in placed)
        {
            fresh[index].CopyFrom(slot);
        }

        Slots.Clear();
        foreach (WheelSlot slot in fresh)
        {
            Slots.Add(slot);
        }
    }

    private (List<(WheelSlot Slot, int Index)> Placed, int Lost) PlanResize(int newCount)
    {
        var placed = new List<(WheelSlot, int)>();
        var taken = new bool[newCount];
        int lost = 0;
        int oldCount = Slots.Count;

        // Slots whose direction exists exactly on the new wheel are placed first, so e.g. a
        // diagonal sound can never steal "right" from the sound that was already pointing right.
        var candidates = new List<(WheelSlot Slot, int Target, double Error, int Index)>();
        for (int i = 0; i < oldCount; i++)
        {
            WheelSlot slot = Slots[i];
            if (slot.IsEmpty)
            {
                continue;
            }
            double exact = i * (double)newCount / oldCount;
            double rounded = Math.Round(exact, MidpointRounding.AwayFromZero);
            candidates.Add((slot, (int)rounded % newCount, Math.Abs(exact - rounded), i));
        }

        foreach (var candidate in candidates.OrderBy(c => c.Error).ThenBy(c => c.Index))
        {
            int free = NearestFree(taken, candidate.Target);
            if (free < 0)
            {
                lost++;
                continue;
            }
            taken[free] = true;
            placed.Add((candidate.Slot, free));
        }
        return (placed, lost);
    }

    private static int NearestFree(bool[] taken, int target)
    {
        int n = taken.Length;
        for (int distance = 0; distance <= n / 2; distance++)
        {
            int clockwise = (target + distance) % n;
            if (!taken[clockwise])
            {
                return clockwise;
            }
            int counter = ((target - distance) % n + n) % n;
            if (!taken[counter])
            {
                return counter;
            }
        }
        return -1;
    }

    private static void ValidateCount(int count)
    {
        if (count is < WheelSelector.MinSlices or > WheelSelector.MaxSlices)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"A wheel needs {WheelSelector.MinSlices}-{WheelSelector.MaxSlices} slices.");
        }
    }
}

/// <summary>A wheel bound to one hotkey.</summary>
public sealed class WheelDefinition : ObservableModel
{
    private string name = "Wheel";
    private int hotkey;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get => name; set => Set(ref name, string.IsNullOrWhiteSpace(value) ? "Wheel" : value.Trim()); }

    /// <summary>Windows virtual-key code (mouse buttons included). 0 = not bound.</summary>
    public int Hotkey
    {
        get => hotkey;
        set
        {
            if (Set(ref hotkey, value))
            {
                OnPropertyChanged(nameof(HotkeyName));
            }
        }
    }

    [JsonIgnore]
    public string HotkeyName => Hotkey == VirtualKeys.None ? "no key" : VirtualKeys.GetName(Hotkey);

    public WheelPage Root { get; set; } = WheelPage.Create(8);
}

/// <summary>Everything the user builds: the sound library and the wheels. Saved as board.json.</summary>
public sealed class SoundBoard
{
    public int Version { get; set; } = 1;

    public ObservableCollection<SoundItem> Sounds { get; set; } = [];

    public ObservableCollection<WheelDefinition> Wheels { get; set; } = [];

    public static SoundBoard CreateDefault()
    {
        var board = new SoundBoard();
        board.Wheels.Add(new WheelDefinition { Name = "Wheel 1", Hotkey = VirtualKeys.Mouse4 });
        return board;
    }

    public SoundItem? FindSound(Guid id) => Sounds.FirstOrDefault(s => s.Id == id);

    public SoundItem? FindSoundByPath(string path) =>
        Sounds.FirstOrDefault(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every slot on every wheel, including slots inside folders.</summary>
    public IEnumerable<WheelSlot> AllSlots()
    {
        foreach (WheelDefinition wheel in Wheels)
        {
            foreach (WheelSlot slot in wheel.Root.Slots)
            {
                yield return slot;
                if (slot.Folder is { } folder)
                {
                    foreach (WheelSlot inner in folder.Slots)
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    public int CountUses(Guid soundId) => AllSlots().Count(s => s.Kind == SlotKind.Sound && s.SoundId == soundId);

    /// <summary>Removes a sound from the library and clears every slot that used it.</summary>
    public void RemoveSound(SoundItem sound)
    {
        foreach (WheelSlot slot in AllSlots().Where(s => s.Kind == SlotKind.Sound && s.SoundId == sound.Id).ToList())
        {
            slot.Clear();
        }
        Sounds.Remove(sound);
        RefreshUsage();
    }

    public void RefreshUsage()
    {
        HashSet<Guid> used = AllSlots()
            .Where(s => s.Kind == SlotKind.Sound && s.SoundId is not null)
            .Select(s => s.SoundId!.Value)
            .ToHashSet();
        foreach (SoundItem sound in Sounds)
        {
            sound.IsOnWheel = used.Contains(sound.Id);
        }
    }

    /// <summary>Repairs anything a hand-edited or older file might contain, and refreshes derived state.</summary>
    public void Normalize()
    {
        Sounds ??= [];
        Wheels ??= [];
        HashSet<Guid> soundIds = Sounds.Select(s => s.Id).ToHashSet();
        foreach (SoundItem sound in Sounds)
        {
            sound.RefreshFileState();
        }
        foreach (WheelDefinition wheel in Wheels)
        {
            wheel.Root ??= WheelPage.Create(8);
            NormalizePage(wheel.Root, soundIds, allowFolders: true);
        }
        RefreshUsage();
    }

    private static void NormalizePage(WheelPage page, HashSet<Guid> soundIds, bool allowFolders)
    {
        page.Slots ??= [];
        for (int i = 0; i < page.Slots.Count; i++)
        {
            page.Slots[i] ??= new WheelSlot();
        }
        if (page.Slots.Count < WheelSelector.MinSlices)
        {
            while (page.Slots.Count < 8)
            {
                page.Slots.Add(new WheelSlot());
            }
        }
        while (page.Slots.Count > WheelSelector.MaxSlices)
        {
            page.Slots.RemoveAt(page.Slots.Count - 1);
        }

        foreach (WheelSlot slot in page.Slots)
        {
            if (slot.Kind == SlotKind.Sound && (slot.SoundId is not Guid id || !soundIds.Contains(id)))
            {
                slot.Clear();
            }
            else if (slot.Kind == SlotKind.Folder && !allowFolders)
            {
                slot.Clear(); // folders are one level deep
            }
            else if (slot.Kind == SlotKind.Folder)
            {
                slot.Folder ??= WheelPage.Create(8);
                NormalizePage(slot.Folder, soundIds, allowFolders: false);
            }
        }
    }
}
