using System.IO;
using System.Windows;
using System.Windows.Threading;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Runtime;
using JriksSoundboard.Core.Settings;

namespace JriksSoundboard.App;

public partial class App : Application
{
    private SoundboardRuntime? runtime;
    private string? snapshotDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            MessageBox.Show(args.ExceptionObject.ToString(), "Jrik's Soundboard crashed", MessageBoxButton.OK, MessageBoxImage.Error);

        var log = new LogStore(Dispatcher);

        // "--snapshot <folder>": render every screen to PNGs with demo data and exit (UI checks).
        // Uses its own temp data folder so real settings/sounds are never touched.
        string? snapshotDir = ArgValue(e.Args, "--snapshot");
        AppSettings settings;
        SoundBoard board;
        if (snapshotDir is not null)
        {
            snapshotDir = Path.GetFullPath(snapshotDir);
            snapshotDirectory = snapshotDir;
            Directory.CreateDirectory(snapshotDir);
            AppPaths.DataDirectory = Path.Combine(snapshotDir, "data");
            settings = new AppSettings();
            board = DemoBoard.Create(Path.Combine(snapshotDir, "demo-sounds"));
        }
        else
        {
            settings = LoadOrStartFresh(() => SettingsStore.Load(AppPaths.SettingsFile), AppPaths.SettingsFile, () => new AppSettings(), log);
            board = LoadOrStartFresh(() => BoardStore.Load(AppPaths.BoardFile), AppPaths.BoardFile, SoundBoard.CreateDefault, log);
        }

        runtime = new SoundboardRuntime(settings, board, AppPaths.SettingsFile, AppPaths.BoardFile)
        {
            PersistenceEnabled = snapshotDir is null,
            AppVersion = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion,
        };
        runtime.Log += log.Add;

        var window = new MainWindow(runtime, log, snapshotDir);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        runtime?.Dispose();
        base.OnExit(e);
    }

    private static T LoadOrStartFresh<T>(Func<T> load, string path, Func<T> fresh, LogStore log)
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            string backup = path + ".bad";
            string message;
            try
            {
                File.Copy(path, backup, overwrite: true);
                message = $"{Path.GetFileName(path)} couldn't be read ({ex.Message}).\n\nStarting fresh; the old file was kept as:\n{backup}";
            }
            catch (Exception copyEx)
            {
                message = $"{Path.GetFileName(path)} couldn't be read ({ex.Message}) and couldn't be backed up ({copyEx.Message}).\n\nStarting fresh.";
            }
            log.Add("⚠ " + message.Replace("\n", " "));
            MessageBox.Show(message, "Jrik's Soundboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return fresh();
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (snapshotDirectory is not null)
        {
            // Automated UI check: record the error and quit instead of stacking dialogs on screen.
            File.AppendAllText(Path.Combine(snapshotDirectory, "snapshot-error.txt"), e.Exception + Environment.NewLine + Environment.NewLine);
            Shutdown(1);
            return;
        }
        MessageBox.Show(e.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
