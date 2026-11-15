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
            foreach (string tab in new[] { "Audio", "Microphone", "Wheel", "Overlay", "Hotkeys", "Diagnostics" })
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
