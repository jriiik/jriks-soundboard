using System.Windows;
using JriksSoundboard.Core.Runtime;

namespace JriksSoundboard.App;

/// <summary>"Press a key" dialog. The key is read through the same passive Raw Input listener, so mouse buttons work too.</summary>
public partial class KeyCaptureWindow : Window
{
    private KeyCaptureWindow(string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;
    }

    /// <summary>Returns the pressed virtual-key code, or null if cancelled.</summary>
    public static async Task<int?> CaptureAsync(Window owner, SoundboardRuntime runtime, string prompt)
    {
        using var cancel = new CancellationTokenSource();
        Task<int?> capture = runtime.CaptureNextKeyAsync(cancel.Token);

        var dialog = new KeyCaptureWindow(prompt) { Owner = owner };
        dialog.Loaded += (_, _) =>
        {
            if (capture.IsCompleted)
            {
                dialog.Close();
            }
        };
        _ = capture.ContinueWith(
            _ => dialog.Dispatcher.BeginInvoke(() =>
            {
                if (dialog.IsLoaded)
                {
                    dialog.Close();
                }
            }),
            TaskScheduler.Default);

        dialog.ShowDialog();
        cancel.Cancel(); // closed by the user → stop waiting
        return await capture;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
