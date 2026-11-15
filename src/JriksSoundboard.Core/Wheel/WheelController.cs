using System.Diagnostics;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;

namespace JriksSoundboard.Core.Wheel;

public enum WheelCloseReason
{
    /// <summary>Released while pointing at a sound — play it.</summary>
    SoundChosen,

    /// <summary>Released inside the deadzone.</summary>
    ReleasedInCentre,

    ReleasedOnEmptySlot,

    /// <summary>Released on a folder before it opened.</summary>
    ReleasedOnFolder,

    /// <summary>Held open longer than the auto-cancel time.</summary>
    TimedOut,

    /// <summary>The cancel key was pressed while the wheel was open.</summary>
    CancelKeyPressed,

    /// <summary>Closed by the app (e.g. while you rebind a key).</summary>
    Cancelled,
}

/// <param name="AutoCancelAfter">Close the wheel (choosing nothing) after this long; null = never.</param>
/// <param name="FolderDwell">How long to rest on a folder before it opens.</param>
/// <param name="Deadzone">Raw mouse counts before a direction counts.</param>
/// <param name="Radius">Max virtual stick distance.</param>
/// <param name="RestartTimerOnHover">Pointing at a different slice gives the auto-cancel timer its full time again,
/// so it only runs out once you stop browsing.</param>
/// <param name="CancelKey">Key that closes an open wheel without playing anything (0 = none).
/// May be another wheel's hotkey: it only acts as "cancel" while a wheel is open.</param>
public sealed record WheelOptions(
    TimeSpan? AutoCancelAfter,
    TimeSpan FolderDwell,
    double Deadzone,
    double Radius,
    bool RestartTimerOnHover = true,
    int CancelKey = 0,
    TimeSpan? PreviewAfter = null)
{
    public static readonly WheelOptions Default = new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(350), 60, 240);
}

/// <summary>Everything the overlay needs to draw the open wheel.</summary>
public sealed record WheelViewState(
    bool IsOpen,
    WheelSnapshot? Wheel,
    PageSnapshot? Page,
    string? FolderName,
    double StickX,
    double StickY,
    double Deadzone,
    double Radius,
    int? HoveredSlot,
    double FolderDwellProgress,
    double? TimeRemainingFraction,
    bool IsPreviewing = false,
    double PreviewProgress = 0)
{
    public static readonly WheelViewState Closed = new(false, null, null, null, 0, 0, 0, 1, null, 0, null);
}

/// <summary>
/// The wheel state machine: hotkey down opens the wheel bound to it, mouse movement points at a
/// slice, resting on a folder opens it, release picks (or cancels), and holding too long
/// auto-cancels. Pure logic with an injectable clock; the host calls <see cref="Tick"/>
/// regularly while a wheel is open. Thread-safe. Events are raised outside the lock.
/// </summary>
public sealed class WheelController
{
    private readonly object gate = new();
    private readonly Func<TimeSpan> clock;
    private readonly WheelSelector selector = new();
    private BoardSnapshot board = BoardSnapshot.Empty;
    private WheelOptions options = WheelOptions.Default;

    private WheelSnapshot? wheel;
    private PageSnapshot? page;
    private string? folderName;
    private TimeSpan timerStartedAt;
    private TimeSpan hoverSince;
    private int? hovered;
    private int suppressedKey;
    private bool previewing; // a headphone preview is playing for the current hover
    private bool previewed;  // the preview already fired for the current hover (once per visit)

    public WheelController(Func<TimeSpan>? clock = null)
    {
        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            clock = () => stopwatch.Elapsed;
        }
        this.clock = clock;
        selector.Configure(options.Deadzone, options.Radius);
    }

    public event Action<WheelSnapshot>? Opened;

    /// <summary>Wheel closed. The slot is only set for <see cref="WheelCloseReason.SoundChosen"/>.</summary>
    public event Action<WheelSnapshot, WheelCloseReason, SlotSnapshot?>? Closed;

    /// <summary>Start a headphone preview of this slot, or stop the current one (null).</summary>
    public event Action<SlotSnapshot?>? PreviewChanged;

    public BoardSnapshot Board
    {
        get { lock (gate) return board; }
        set { lock (gate) board = value ?? BoardSnapshot.Empty; }
    }

    public WheelOptions Options
    {
        get { lock (gate) return options; }
        set
        {
            lock (gate)
            {
                options = value;
                selector.Configure(value.Deadzone, value.Radius);
            }
        }
    }

    public bool IsOpen { get { lock (gate) return wheel is not null; } }

    /// <summary>Returns true if the key belongs to the wheel system (so nothing else should act on it).</summary>
    public bool ButtonDown(int virtualKey)
    {
        WheelSnapshot? opened = null;
        WheelSnapshot? cancelled = null;
        lock (gate)
        {
            if (virtualKey == VirtualKeys.None)
            {
                return false;
            }

            if (wheel is not null)
            {
                if (virtualKey != options.CancelKey)
                {
                    return virtualKey == wheel.Hotkey; // another wheel's key while one is open: ignored
                }
                cancelled = wheel;
                suppressedKey = wheel.Hotkey; // the wheel key is still held; its release must do nothing
                selector.Cancel();
                ResetLocked();
            }
            else if (virtualKey == suppressedKey)
            {
                return true;
            }
            else
            {
                opened = board.WheelForKey(virtualKey);
                if (opened is null)
                {
                    return false;
                }
                wheel = opened;
                OpenPageLocked(opened.Root, null);
            }
        }

        if (cancelled is not null)
        {
            Closed?.Invoke(cancelled, WheelCloseReason.CancelKeyPressed, null);
        }
        else if (opened is not null)
        {
            Opened?.Invoke(opened);
        }
        return true;
    }

    public bool ButtonUp(int virtualKey)
    {
        WheelSnapshot closedWheel;
        WheelCloseReason reason;
        SlotSnapshot? chosen;
        lock (gate)
        {
            if (virtualKey != VirtualKeys.None && virtualKey == suppressedKey)
            {
                suppressedKey = VirtualKeys.None; // release after a timeout: nothing to do
                return true;
            }
            if (wheel is null || page is null || virtualKey != wheel.Hotkey)
            {
                return false;
            }

            int? index = selector.End();
            SlotSnapshot? slot = index is int i && i < page.Slots.Count ? page.Slots[i] : null;
            reason = slot?.Kind switch
            {
                null => WheelCloseReason.ReleasedInCentre,
                SlotKind.Sound => WheelCloseReason.SoundChosen,
                SlotKind.Folder => WheelCloseReason.ReleasedOnFolder,
                _ => WheelCloseReason.ReleasedOnEmptySlot,
            };
            chosen = reason == WheelCloseReason.SoundChosen ? slot : null;
            closedWheel = wheel;
            ResetLocked();
        }

        Closed?.Invoke(closedWheel, reason, chosen);
        return true;
    }

    public void MouseMoved(int dx, int dy)
    {
        PreviewChange? change;
        lock (gate)
        {
            if (wheel is null)
            {
                return;
            }
            selector.AddDelta(dx, dy);
            change = UpdateHoverLocked(clock());
        }
        if (change is not null)
        {
            PreviewChanged?.Invoke(change.Slot);
        }
    }

    /// <summary>Advances time-based behaviour (folder dwell, auto-cancel). Call every ~15 ms while open.</summary>
    public void Tick()
    {
        WheelSnapshot? timedOut = null;
        PreviewChange? change;
        lock (gate)
        {
            if (wheel is null)
            {
                return;
            }

            TimeSpan now = clock();
            change = UpdateHoverLocked(now);

            if (options.AutoCancelAfter is { } limit && now - timerStartedAt >= limit)
            {
                timedOut = wheel;
                suppressedKey = wheel.Hotkey; // the key is still held; its release must do nothing
                selector.Cancel();
                ResetLocked();
            }
        }

        if (change is not null)
        {
            PreviewChanged?.Invoke(change.Slot);
        }
        if (timedOut is not null)
        {
            Closed?.Invoke(timedOut, WheelCloseReason.TimedOut, null);
        }
    }

    /// <summary>Closes an open wheel without choosing anything.</summary>
    public void Cancel()
    {
        WheelSnapshot? cancelled = null;
        lock (gate)
        {
            if (wheel is not null)
            {
                cancelled = wheel;
                suppressedKey = wheel.Hotkey;
                selector.Cancel();
                ResetLocked();
            }
        }

        if (cancelled is not null)
        {
            Closed?.Invoke(cancelled, WheelCloseReason.Cancelled, null);
        }
    }

    public WheelViewState GetState()
    {
        lock (gate)
        {
            if (wheel is null || page is null)
            {
                return WheelViewState.Closed;
            }

            TimeSpan now = clock();
            WheelState stick = selector.GetState();

            double dwell = 0;
            if (hovered is int h && folderName is null && page.Slots[h].Kind == SlotKind.Folder && options.FolderDwell > TimeSpan.Zero)
            {
                dwell = Math.Clamp((now - hoverSince) / options.FolderDwell, 0, 1);
            }

            double? remaining = options.AutoCancelAfter is { } limit && limit > TimeSpan.Zero
                ? Math.Clamp(1 - (now - timerStartedAt) / limit, 0, 1)
                : null;

            double previewProgress = 0;
            if (hovered is int ph && !previewed && page.Slots[ph].Kind == SlotKind.Sound
                && options.PreviewAfter is { } after && after > TimeSpan.Zero)
            {
                previewProgress = Math.Clamp((now - hoverSince) / after, 0, 1);
            }

            return new WheelViewState(true, wheel, page, folderName, stick.X, stick.Y, stick.Deadzone, stick.Radius,
                stick.SelectedSlice, dwell, remaining, previewing, previewProgress);
        }
    }

    private void OpenPageLocked(PageSnapshot newPage, string? newFolderName)
    {
        page = newPage;
        folderName = newFolderName;
        selector.SliceCount = Math.Clamp(newPage.Slots.Count, WheelSelector.MinSlices, WheelSelector.MaxSlices);
        selector.Begin();
        timerStartedAt = clock();
        hoverSince = timerStartedAt;
        hovered = null;
        previewing = false;
        previewed = false;
    }

    /// <returns>A preview start/stop to announce once the lock is released, or null.</returns>
    private PreviewChange? UpdateHoverLocked(TimeSpan now)
    {
        int? current = selector.GetState().SelectedSlice;
        if (current != hovered)
        {
            bool wasPreviewing = previewing;
            hovered = current;
            hoverSince = now;
            previewing = false;
            previewed = false;
            if (current is not null && options.RestartTimerOnHover)
            {
                timerStartedAt = now; // still browsing: give the full time again
            }
            return wasPreviewing ? PreviewChange.Stop : null;
        }

        if (hovered is not int h)
        {
            return null;
        }
        SlotSnapshot slot = page!.Slots[h];

        // Resting on a folder (top level only) opens it and recentres the stick.
        if (folderName is null && slot is { Kind: SlotKind.Folder, Folder: { } sub } && now - hoverSince >= options.FolderDwell)
        {
            OpenPageLocked(sub, slot.Label);
            return null;
        }

        // Resting on a sound plays it in your headphones (once per visit to that slice).
        if (!previewed && slot.Kind == SlotKind.Sound && options.PreviewAfter is { } after && now - hoverSince >= after)
        {
            previewed = true;
            previewing = true;
            if (options.RestartTimerOnHover)
            {
                timerStartedAt = now; // time to decide after hearing it
            }
            return new PreviewChange(slot);
        }
        return null;
    }

    private sealed record PreviewChange(SlotSnapshot? Slot)
    {
        public static readonly PreviewChange Stop = new((SlotSnapshot?)null);
    }

    private void ResetLocked()
    {
        wheel = null;
        page = null;
        folderName = null;
        hovered = null;
        previewing = false;
        previewed = false;
    }
}
