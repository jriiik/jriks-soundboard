using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Runtime;
using JriksSoundboard.Core.Settings;
using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.App;

/// <summary>
/// The pop-up wheel. A separate see-through, always-on-top window that never takes focus and
/// lets clicks pass through — it is positioned over the game (or over this app) but is NOT
/// drawn inside the game, so nothing touches the game process.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly TimeSpan CancelledLinger = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan TimerLinger = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan DemoLength = TimeSpan.FromSeconds(3);

    private readonly SoundboardRuntime runtime;
    private readonly Window appWindow;
    private readonly DispatcherTimer timer;
    private IntPtr handle;
    private Phase phase = Phase.Hidden;
    private DateTime phaseChanged;
    private PageSnapshot? drawnPage;

    public OverlayWindow(SoundboardRuntime runtime, Window appWindow)
    {
        this.runtime = runtime;
        this.appWindow = appWindow;
        InitializeComponent();

        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => OnTick();

        runtime.WheelOpened += _ => Dispatcher.BeginInvoke(ShowWheel);
        runtime.WheelClosed += (_, reason, _) => Dispatcher.BeginInvoke(() => OnWheelClosed(reason));
    }

    private enum Phase
    {
        Hidden,
        Wheel,
        Cancelled,
        Timer,
        Demo,
    }

    /// <summary>Creates the (hidden) window handle up front so the first pop-up is instant.</summary>
    public void Initialize()
    {
        if (handle == IntPtr.Zero)
        {
            handle = new WindowInteropHelper(this).EnsureHandle();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long style = NativeWin32.GetExStyle(hwnd);
        NativeWin32.SetExStyle(hwnd, style | NativeWin32.WS_EX_TRANSPARENT | NativeWin32.WS_EX_NOACTIVATE | NativeWin32.WS_EX_TOOLWINDOW);
    }

    /// <summary>Shows a sample wheel for a few seconds (Settings → Overlay → Preview).</summary>
    public void ShowDemo()
    {
        ApplyAppearance();
        ApplyDemoState();
        SetPhase(Phase.Demo);
        PositionAndShow();
    }

    /// <summary>Lays out the demo state off-screen and returns the content to render (UI snapshots).</summary>
    public FrameworkElement BuildDemoForSnapshot(bool cancelled = false)
    {
        ApplyAppearance();
        ApplyDemoState();
        if (cancelled)
        {
            Wheel.HighlightedSlot = null;
            Wheel.Stick = null;
            Wheel.TimeRemaining = 0;
            Wheel.CenterText = "Cancelled";
        }
        Wheel.Visibility = Visibility.Visible;
        TimerPill.Visibility = Visibility.Visible;
        TimerText.Text = "Nice shot · 0.8s";
        TimerBar.Value = 35;
        var size = new Size(Width, Height);
        Root.Measure(size);
        Root.Arrange(new Rect(size));
        Root.UpdateLayout();
        return Root;
    }

    private void ApplyAppearance()
    {
        AppSettings settings = runtime.Settings;
        double size = Math.Clamp(settings.OverlaySize, 200, 700);
        Wheel.Width = size;
        Wheel.Height = size;
        Width = size + 160; // extra width so long names fit above the wheel (the window is click-through)
        Height = size + 174;
        NameChip.MaxWidth = Width - 20;
        Root.Opacity = Math.Clamp(settings.OverlayOpacity, 0.3, 1.0);
        TimerHint.Visibility = settings.MicMode == MicMode.GamePushToTalk ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyDemoState()
    {
        WheelSnapshot? sample = runtime.Wheels.Board.Wheels.FirstOrDefault();
        drawnPage = null;
        Wheel.Slots = sample is null
            ? Enumerable.Repeat(WheelSlotVisual.Empty, 8).ToList()
            : sample.Root.Slots.Select(s => new WheelSlotVisual(s.Label, s.Kind)).ToList();
        Wheel.HighlightedSlot = 1 % Math.Max(1, Wheel.Slots.Count);
        Wheel.Stick = new System.Windows.Vector(0.62, -0.62);
        Wheel.DeadzoneFraction = runtime.Settings.Deadzone / Math.Max(1, runtime.Settings.Radius);
        Wheel.TimeRemaining = runtime.Settings.AutoCancelEnabled ? 0.6 : null;
        Wheel.DwellProgress = 0;
        Wheel.CenterText = sample?.Name ?? "Preview";
        ShowName(sample is not null && Wheel.HighlightedSlot is int h && h < sample.Root.Slots.Count ? Describe(sample.Root.Slots[h]) : null);
    }

    private void ShowName(string? name)
    {
        NameText.Text = name ?? string.Empty;
        NameChip.Visibility = string.IsNullOrEmpty(name) ? Visibility.Hidden : Visibility.Visible;
    }

    private static string? Describe(SlotSnapshot slot) => slot.Kind switch
    {
        SlotKind.Sound => slot.Label,
        SlotKind.Folder => $"{slot.Label}  ·  folder",
        _ => null,
    };

    private void ShowWheel()
    {
        if (runtime.Settings.OverlayMode == OverlayMode.Off)
        {
            return;
        }
        ApplyAppearance();
        drawnPage = null;
        UpdateWheel();
        SetPhase(Phase.Wheel);
        PositionAndShow();
    }

    private void OnWheelClosed(WheelCloseReason reason)
    {
        if (phase != Phase.Wheel)
        {
            return;
        }

        if (reason == WheelCloseReason.SoundChosen && runtime.Settings.ShowPlaybackTimer)
        {
            TimerText.Text = "…";
            TimerBar.Value = 0;
            SetPhase(Phase.Timer);
        }
        else if (reason is WheelCloseReason.TimedOut or WheelCloseReason.CancelKeyPressed)
        {
            Wheel.HighlightedSlot = null;
            Wheel.Stick = null;
            Wheel.TimeRemaining = 0;
            Wheel.DwellProgress = 0;
            Wheel.CenterText = "Cancelled";
            SetPhase(Phase.Cancelled);
        }
        else
        {
            HideOverlay();
        }
    }

    private void SetPhase(Phase next)
    {
        phase = next;
        phaseChanged = DateTime.UtcNow;
        Wheel.Visibility = next is Phase.Timer ? Visibility.Collapsed : Visibility.Visible;
        TimerPill.Visibility = next is Phase.Timer ? Visibility.Visible : Visibility.Collapsed;
        if (next is not (Phase.Wheel or Phase.Demo))
        {
            ShowName(null);
        }
        timer.Start();
    }

    private void OnTick()
    {
        TimeSpan inPhase = DateTime.UtcNow - phaseChanged;
        switch (phase)
        {
            case Phase.Wheel:
                UpdateWheel();
                break;

            case Phase.Cancelled:
                if (inPhase > CancelledLinger)
                {
                    HideOverlay();
                }
                break;

            case Phase.Timer:
                SoundPlayback? playback = runtime.Engine.LatestPlayback;
                if (playback is { IsFinished: false })
                {
                    TimerText.Text = $"{runtime.LastPlayedName ?? playback.Sound.Name} · {playback.Remaining.TotalSeconds:0.0}s";
                    TimerBar.Value = playback.Progress * 100;
                    phaseChanged = DateTime.UtcNow; // stay while it plays
                }
                else if (inPhase > TimerLinger)
                {
                    HideOverlay();
                }
                break;

            case Phase.Demo:
                if (inPhase > DemoLength)
                {
                    HideOverlay();
                }
                break;

            default:
                timer.Stop();
                break;
        }
    }

    private void UpdateWheel()
    {
        WheelViewState state = runtime.Wheels.GetState();
        if (!state.IsOpen || state.Page is null)
        {
            return;
        }

        if (!ReferenceEquals(state.Page, drawnPage))
        {
            drawnPage = state.Page;
            Wheel.Slots = state.Page.Slots.Select(s => new WheelSlotVisual(s.Label, s.Kind)).ToList();
        }
        double radius = Math.Max(1, state.Radius);
        Wheel.HighlightedSlot = state.HoveredSlot;
        Wheel.Stick = new System.Windows.Vector(state.StickX / radius, state.StickY / radius);
        Wheel.DeadzoneFraction = state.Deadzone / radius;
        Wheel.TimeRemaining = state.TimeRemainingFraction;
        Wheel.DwellProgress = state.FolderDwellProgress;
        Wheel.CenterText = state.FolderName ?? state.Wheel?.Name;
        ShowName(state.HoveredSlot is int h && h < state.Page.Slots.Count ? Describe(state.Page.Slots[h]) : null);
    }

    private void PositionAndShow()
    {
        Initialize();
        (int cx, int cy) = TargetCentre();

        NativeWin32.GetWindowRect(handle, out NativeWin32.RECT rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        double scale = Height > 0 && height > 0 ? height / Height : 1.0;
        double wheelCentreY = (Wheel.Margin.Top + Wheel.Height / 2) * scale;

        // Centre the wheel (not the whole window) on the target, without ever activating.
        NativeWin32.SetWindowPos(handle, NativeWin32.HWND_TOPMOST, cx - width / 2, (int)(cy - wheelCentreY), 0, 0,
            NativeWin32.SWP_NOSIZE | NativeWin32.SWP_NOACTIVATE);
        if (!IsVisible)
        {
            Show();
        }
    }

    /// <summary>Centre of the game window (foreground) or of this app's window, in screen pixels.</summary>
    private (int X, int Y) TargetCentre()
    {
        IntPtr target = IntPtr.Zero;
        if (runtime.Settings.OverlayMode == OverlayMode.OverAppWindow)
        {
            IntPtr app = new WindowInteropHelper(appWindow).Handle;
            if (app != IntPtr.Zero && !NativeWin32.IsIconic(app))
            {
                target = app;
            }
        }
        if (target == IntPtr.Zero)
        {
            IntPtr foreground = NativeWin32.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != handle)
            {
                target = foreground;
            }
        }

        if (target != IntPtr.Zero && NativeWin32.GetWindowRect(target, out NativeWin32.RECT r) && r.Right > r.Left)
        {
            return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        }
        return (NativeWin32.GetSystemMetrics(NativeWin32.SM_CXSCREEN) / 2, NativeWin32.GetSystemMetrics(NativeWin32.SM_CYSCREEN) / 2);
    }

    private void HideOverlay()
    {
        phase = Phase.Hidden;
        timer.Stop();
        Hide();
    }
}
