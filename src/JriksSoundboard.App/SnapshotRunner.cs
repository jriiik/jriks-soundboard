using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JriksSoundboard.App;

/// <summary>
/// "--snapshot &lt;folder&gt;" mode: renders the main window, every settings tab and the overlay to
/// PNG files, then exits. Lets the UI be checked without clicking through it by hand.
/// </summary>
internal static class SnapshotRunner
{
    public static async void Run(MainWindow main, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            await Task.Delay(400);
            main.PrepareSnapshot();
            await Idle(main);
            SaveWindow(main, Path.Combine(folder, "main.png"));

            SettingsWindow settings = main.OpenSettings();
            foreach (string tab in new[] { "Audio", "Microphone", "Wheel", "Overlay", "Hotkeys", "Backup", "Diagnostics" })
            {
                settings.SelectTab(tab);
                await Task.Delay(150);
                await Idle(settings);
                SaveWindow(settings, Path.Combine(folder, $"settings-{tab.ToLowerInvariant()}.png"));
            }
            settings.Close();

            var gameGrey = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x5A));
            SaveElement(main.Overlay.BuildDemoForSnapshot(), Path.Combine(folder, "overlay.png"), gameGrey);
            SaveElement(main.Overlay.BuildDemoForSnapshot(cancelled: true), Path.Combine(folder, "overlay-cancelled.png"), gameGrey);

            // README showcase: animation frames + stills of the wheel in use.
            new ShowcaseRenderer(main.Runtime.Wheels.Board.Wheels[0]).Render(Path.Combine(folder, "showcase"));

            // Label stress test: a 16-slice wheel full of long file-style names.
            SaveElement(LabelStressWheel(), Path.Combine(folder, "labels-16.png"), new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "snapshot-error.txt"), ex.ToString());
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static FrameworkElement LabelStressWheel()
    {
        string[] names =
        [
            "whatdoyoumean_1_fast_bored", "what_are_you_talking_about_1_fast_bored", "im_muting_you", "shut_up",
            "never", "nope", "Emotional damage", "Why are you running", "It's free real estate",
            "Directed by Robert B. Weide", "Mission failed, we'll get em next time", "Rush B no stop",
            "To be continued", "Windows XP shutdown", "bruh", "Vine boom",
        ];
        var wheel = new WheelView
        {
            Width = 480,
            Height = 480,
            CenterText = "Sent 1",
            HighlightedSlot = 0,
            Slots = names.Select(n => new WheelSlotVisual(n, JriksSoundboard.Core.Board.SlotKind.Sound)).ToList(),
        };
        var size = new Size(480, 480);
        wheel.Measure(size);
        wheel.Arrange(new Rect(size));
        wheel.UpdateLayout();
        return wheel;
    }

    private static Task Idle(Window window) =>
        window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void SaveWindow(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        Thickness m = root.Margin;
        var size = new Size(root.ActualWidth + m.Left + m.Right, root.ActualHeight + m.Top + m.Bottom);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(new Point(m.Left, m.Top), new Size(root.ActualWidth, root.ActualHeight)));
        }
        Save(visual, size, path);
    }

    private static void SaveElement(FrameworkElement element, string path, Brush background)
    {
        var size = new Size(element.ActualWidth, element.ActualHeight);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null, new Rect(size));
        }
        Save(visual, size, path);
    }

    private static void Save(Visual visual, Size size, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
