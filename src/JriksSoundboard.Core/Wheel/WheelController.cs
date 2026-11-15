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

    /// <summary>Closed by the app (e.g. while you rebind a key).</summary>
    Cancelled,
}

/// <param name="AutoCancelAfter">Close the wheel (choosing nothing) after this long; null = never.</param>
/// <param name="FolderDwell">How long to rest on a folder before it opens.</param>
/// <param name="Deadzone">Raw mouse counts before a direction counts.</param>
/// <param name="Radius">Max virtual stick distance.</param>
public sealed record WheelOptions(TimeSpan? AutoCancelAfter, TimeSpan FolderDwell, double Deadzone, double Radius)
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
    double? TimeRemainingFraction)
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
    private TimeSpan pageOpenedAt;
    private TimeSpan hoverSince;
    private int? hovered;
    private int suppressedKey;

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
        WheelSnapshot? opened;
        lock (gate)
        {
            if (virtualKey == VirtualKeys.None)
            {
                return false;
            }
            if (wheel is not null)
            {
                return virtualKey == wheel.Hotkey; // another wheel's key while one is open: ignored
            }
            if (virtualKey == suppressedKey)
            {
                return true;
            }

            opened = board.WheelForKey(virtualKey);
            if (opened is null)
            {
                return false;
            }
            wheel = opened;
            OpenPageLocked(opened.Root, null);
        }

        Opened?.Invoke(opened);
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
        lock (gate)
        {
            if (wheel is null)
            {
                return;
            }
            selector.AddDelta(dx, dy);
            UpdateHoverLocked(clock());
        }
    }

    /// <summary>Advances time-based behaviour (folder dwell, auto-cancel). Call every ~15 ms while open.</summary>
    public void Tick()
    {
        WheelSnapshot? timedOut = null;
        lock (gate)
        {
            if (wheel is null)
            {
                return;
            }

            TimeSpan now = clock();
            UpdateHoverLocked(now);

            if (options.AutoCancelAfter is { } limit && now - pageOpenedAt >= limit)
            {
                timedOut = wheel;
                suppressedKey = wheel.Hotkey; // the key is still held; its release must do nothing
                selector.Cancel();
                ResetLocked();
            }
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
                ? Math.Clamp(1 - (now - pageOpenedAt) / limit, 0, 1)
                : null;

            return new WheelViewState(true, wheel, page, folderName, stick.X, stick.Y, stick.Deadzone, stick.Radius,
                stick.SelectedSlice, dwell, remaining);
        }
    }

    private void OpenPageLocked(PageSnapshot newPage, string? newFolderName)
    {
        page = newPage;
        folderName = newFolderName;
        selector.SliceCount = Math.Clamp(newPage.Slots.Count, WheelSelector.MinSlices, WheelSelector.MaxSlices);
        selector.Begin();
        pageOpenedAt = clock();
        hoverSince = pageOpenedAt;
        hovered = null;
    }

    private void UpdateHoverLocked(TimeSpan now)
    {
        int? current = selector.GetState().SelectedSlice;
        if (current != hovered)
        {
            hovered = current;
            hoverSince = now;
            return;
        }

        // Resting on a folder (top level only) opens it and recentres the stick.
        if (hovered is int h
            && folderName is null
            && page!.Slots[h] is { Kind: SlotKind.Folder, Folder: { } sub } folderSlot
            && now - hoverSince >= options.FolderDwell)
        {
            OpenPageLocked(sub, folderSlot.Label);
        }
    }

    private void ResetLocked()
    {
        wheel = null;
        page = null;
        folderName = null;
        hovered = null;
    }
}
