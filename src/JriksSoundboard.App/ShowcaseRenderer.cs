using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.App;

/// <summary>
/// Renders the README showcase in snapshot mode: animation frames of the wheel being used
/// (flick → play, then folder → open → flick → play) plus a few stills, all with a transparent
/// background so they look right on light and dark pages.
/// </summary>
internal sealed class ShowcaseRenderer
{
    private const double WheelSize = 400;
    private const double CanvasWidth = 440;
    private const double CaptionHeight = 52;
    private const double PillHeight = 100;
    private const double Deadzone = 0.25;
    private const double AutoCancelMs = 3000;
    private const double FlickLength = 0.93; // a real flick usually clamps near the rim (and stays off the label)
    private const double TotalMs = 12800;
    public const int FramesPerSecond = 15;

    private readonly Grid root;
    private readonly WheelView wheel;
    private readonly Border captionChip;
    private readonly TextBlock captionText;
    private readonly Border pill;
    private readonly TextBlock pillText;
    private readonly ProgressBar pillBar;
    private readonly PageSnapshot rootPage;
    private readonly PageSnapshot folderPage;
    private readonly string wheelName;
    private readonly string folderName;
    private readonly int folderIndex;
    private readonly int firstSoundIndex;
    private readonly int folderSoundIndex;
    private readonly Dictionary<PageSnapshot, List<WheelSlotVisual>> visuals = [];

    public ShowcaseRenderer(WheelSnapshot demoWheel)
    {
        rootPage = demoWheel.Root;
        wheelName = demoWheel.Name;
        folderIndex = IndexOf(rootPage, s => s.Kind == SlotKind.Folder);
        SlotSnapshot folderSlot = rootPage.Slots[folderIndex];
        folderPage = folderSlot.Folder!;
        folderName = folderSlot.Label;
        firstSoundIndex = 1 < rootPage.Slots.Count && rootPage.Slots[1].Kind == SlotKind.Sound ? 1 : IndexOf(rootPage, s => s.Kind == SlotKind.Sound);
        folderSoundIndex = IndexOf(folderPage, s => s.Kind == SlotKind.Sound);

        wheel = new WheelView
        {
            IsOverlayStyle = true,
            Width = WheelSize,
            Height = WheelSize,
            DeadzoneFraction = Deadzone,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, CaptionHeight, 0, 0),
        };

        Brush chipBackground = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x14, 0x18));
        captionText = new TextBlock { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold };
        captionChip = new Border
        {
            Background = chipBackground,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(16, 7, 16, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Child = captionText,
        };

        pillText = new TextBlock { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        pillBar = new ProgressBar { Height = 4, Maximum = 100, Margin = new Thickness(0, 8, 0, 0) };
        var pillHint = new TextBlock
        {
            Text = "keep holding push-to-talk",
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
        };
        pill = new Border
        {
            Background = chipBackground,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
            Child = new StackPanel { Width = 250, Children = { pillText, pillBar, pillHint } },
        };

        root = new Grid { Width = CanvasWidth, Height = CaptionHeight + WheelSize + PillHeight, Background = Brushes.Transparent };
        root.Children.Add(wheel);
        root.Children.Add(captionChip);
        root.Children.Add(pill);
    }

    private sealed record Frame(string Caption, PageSnapshot? Page, string Centre, Vector Stick, double Dwell, double? Ring, string? PillText, double PillProgress);

    /// <summary>Writes frames/frame_000.png… plus still images into <paramref name="folder"/>.</summary>
    public void Render(string folder)
    {
        string frames = Path.Combine(folder, "frames");
        Directory.CreateDirectory(frames);

        int index = 0;
        for (double t = 0; t < TotalMs; t += 1000.0 / FramesPerSecond)
        {
            Save(StateAt(t), Path.Combine(frames, $"frame_{index++:000}.png"), scale: 1);
        }

        Save(StateAt(2200), Path.Combine(folder, "wheel-hero.png"), scale: 1.5);
        Save(StateAt(5500 + 1400 + 600), Path.Combine(folder, "folder-rest.png"), scale: 1.5);
        Save(StateAt(5500 + 2300 + 1800), Path.Combine(folder, "folder-open.png"), scale: 1.5);
        Save(new Frame("Changed your mind? Just wait…", rootPage, wheelName, new Vector(0.04, -0.03), 0, 0.12, null, 0),
            Path.Combine(folder, "auto-cancel.png"), scale: 1.5);
    }

    private Frame StateAt(double t)
    {
        Vector toSound = Direction(firstSoundIndex, rootPage.Slots.Count) * FlickLength;
        Vector toFolder = Direction(folderIndex, rootPage.Slots.Count) * FlickLength;
        Vector inFolder = Direction(folderSoundIndex, folderPage.Slots.Count) * FlickLength;
        string firstSound = rootPage.Slots[firstSoundIndex].Label;
        string folderSound = folderPage.Slots[folderSoundIndex].Label;
        var blank = new Frame(string.Empty, null, string.Empty, default, 0, null, null, 0);

        // Timings are stretched compared to real use so every caption stays up long enough to read.
        // The ring refills when the stick reaches a slice, like the real auto-cancel timer.

        // Part 1 — pick a sound.
        if (t < 1200) return new("Hold Mouse 4", rootPage, wheelName, default, 0, Ring(t), null, 0);
        if (t < 1700) return new("Flick toward a sound", rootPage, wheelName, toSound * Ease((t - 1200) / 500), 0, Ring(t - 1200), null, 0);
        if (t < 2900) return new("Release to play", rootPage, wheelName, toSound, 0, Ring(t - 1200), null, 0);
        if (t < 4900)
        {
            double done = (t - 2900) / 2000;
            return new("It plays through your mic", null, string.Empty, default, 0, null, $"{firstSound} · {2.0 * (1 - done):0.0}s", done * 100);
        }
        if (t < 5500) return blank;

        // Part 2 — folders.
        double b = t - 5500;
        if (b < 900) return new("Hold Mouse 4", rootPage, wheelName, default, 0, Ring(b), null, 0);
        if (b < 1400) return new("Flick to a folder", rootPage, wheelName, toFolder * Ease((b - 900) / 500), 0, Ring(b - 900), null, 0);
        if (b < 2300) return new("Rest on it…", rootPage, wheelName, toFolder, (b - 1400) / 900, Ring(b - 900), null, 0);

        double c = b - 2300; // folder open: the timer restarts
        if (c < 1000) return new("…and it opens", folderPage, folderName, default, 0, Ring(c), null, 0);
        if (c < 1500) return new("Flick again inside", folderPage, folderName, inFolder * Ease((c - 1000) / 500), 0, Ring(c - 1000), null, 0);
        if (c < 2400) return new("Release", folderPage, folderName, inFolder, 0, Ring(c - 1000), null, 0);

        double d = c - 2400;
        if (d < 1800)
        {
            double done = Math.Min(1, d / 1100);
            string? pill = d < 1100 ? $"{folderSound} · {1.1 * (1 - done):0.0}s" : null;
            return new("Up to 256 sounds on one key", null, string.Empty, default, 0, null, pill, done * 100);
        }
        return blank;
    }

    private void Apply(Frame frame)
    {
        captionText.Text = frame.Caption;
        captionChip.Visibility = frame.Caption.Length > 0 ? Visibility.Visible : Visibility.Hidden;

        wheel.Visibility = frame.Page is null ? Visibility.Hidden : Visibility.Visible;
        if (frame.Page is { } page)
        {
            if (!visuals.TryGetValue(page, out List<WheelSlotVisual>? slots))
            {
                slots = page.Slots.Select(s => new WheelSlotVisual(s.Label, s.Kind)).ToList();
                visuals[page] = slots;
            }
            wheel.Slots = slots;
            wheel.Stick = frame.Stick;
            wheel.HighlightedSlot = frame.Stick.Length >= Deadzone
                ? WheelSelector.SliceForVector(frame.Stick.X, frame.Stick.Y, page.Slots.Count)
                : null;
            wheel.DwellProgress = Math.Clamp(frame.Dwell, 0, 1);
            wheel.TimeRemaining = frame.Ring;
            wheel.CenterText = frame.Centre;
        }

        pill.Visibility = frame.PillText is null ? Visibility.Hidden : Visibility.Visible;
        pillText.Text = frame.PillText ?? string.Empty;
        pillBar.Value = frame.PillProgress;
    }

    private void Save(Frame frame, string path, double scale)
    {
        Apply(frame);
        var size = new Size(root.Width, root.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double Ring(double elapsedMs) => Math.Clamp(1 - elapsedMs / AutoCancelMs, 0, 1);

    private static double Ease(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return 1 - Math.Pow(1 - x, 3);
    }

    private static Vector Direction(int slice, int count)
    {
        double radians = slice * 2 * Math.PI / count;
        return new Vector(Math.Sin(radians), -Math.Cos(radians));
    }

    private static int IndexOf(PageSnapshot page, Func<SlotSnapshot, bool> match)
    {
        for (int i = 0; i < page.Slots.Count; i++)
        {
            if (match(page.Slots[i]))
            {
                return i;
            }
        }
        throw new InvalidOperationException("The demo wheel is missing a slot the showcase needs.");
    }
}
