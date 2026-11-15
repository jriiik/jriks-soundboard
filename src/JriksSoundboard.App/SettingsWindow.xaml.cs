using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Runtime;
using JriksSoundboard.Core.Settings;

namespace JriksSoundboard.App;

/// <summary>All the toggles, kept out of the main window. Changes apply immediately and are saved.</summary>
public partial class SettingsWindow : Window
{
    private readonly SoundboardRuntime runtime;
    private readonly OverlayWindow overlay;
    private readonly DispatcherTimer liveTimer;
    private readonly DispatcherTimer saveDebounce;
    private readonly Action<AudioStatus> statusHandler;
    private bool populating;

    public SettingsWindow(SoundboardRuntime runtime, LogStore log, OverlayWindow overlay)
    {
        this.runtime = runtime;
        this.overlay = overlay;
        InitializeComponent();

        LogList.ItemsSource = log.Entries;
        WheelHotkeyList.ItemsSource = runtime.Board.Wheels;

        statusHandler = _ => Dispatcher.BeginInvoke(UpdateAudioStatus);
        runtime.AudioStatusChanged += statusHandler;

        liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        liveTimer.Tick += (_, _) => UpdateLive();
        saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveDebounce.Tick += (_, _) =>
        {
            saveDebounce.Stop();
            runtime.SaveSettings();
        };

        Loaded += (_, _) => LoadValues();
        Closed += (_, _) =>
        {
            liveTimer.Stop();
            if (saveDebounce.IsEnabled)
            {
                saveDebounce.Stop();
                runtime.SaveSettings();
            }
            runtime.AudioStatusChanged -= statusHandler;
        };
    }

    /// <summary>Raised after a backup replaced the library, wheels and settings.</summary>
    public event Action? ConfigurationLoaded;

    private AppSettings S => runtime.Settings;

    public void SelectTab(string header) =>
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().First(t => (string)t.Header == header);

    private void ScheduleSave()
    {
        saveDebounce.Stop();
        saveDebounce.Start();
    }

    // ───────────────────────── load ─────────────────────────

    private void LoadValues()
    {
        populating = true;
        try
        {
            PopulateDevices();
            MonitorEnabledBox.IsChecked = S.MonitorEnabled;
            MonitorDeviceBox.IsEnabled = S.MonitorEnabled;
            SoundVolumeSlider.Value = S.SoundVolume * 100;
            MonitorVolumeSlider.Value = S.MonitorVolume * 100;
            MicVolumeSlider.Value = S.MicVolume * 100;
            NormalizeBox.IsChecked = S.NormalizeLoudness;
            StopPreviousBox.IsChecked = S.StopPreviousOnPlay;

            RadioButton micRadio = S.MicMode switch
            {
                MicMode.AppPushToTalk => ModeAppPtt,
                MicMode.VoiceActivation => ModeVoice,
                MicMode.Off => ModeOff,
                _ => ModeGamePtt,
            };
            micRadio.IsChecked = true;
            ThresholdSlider.Value = S.VoiceThresholdDb;

            AutoCancelBox.IsChecked = S.AutoCancelEnabled;
            AutoCancelSlider.Value = S.AutoCancelSeconds;
            TimeoutCueBox.IsChecked = S.TimeoutCue;
            RestartOnHoverBox.IsChecked = S.RestartTimerOnHover;
            DeadzoneSlider.Value = S.Deadzone;
            RadiusSlider.Value = S.Radius;
            DwellSlider.Value = S.FolderDwellMs;

            RadioButton overlayRadio = S.OverlayMode switch
            {
                OverlayMode.OverAppWindow => OverlayApp,
                OverlayMode.Off => OverlayOff,
                _ => OverlayGame,
            };
            overlayRadio.IsChecked = true;
            OverlaySizeSlider.Value = S.OverlaySize;
            OverlayOpacitySlider.Value = S.OverlayOpacity * 100;
            PlaybackTimerBox.IsChecked = S.ShowPlaybackTimer;

            UpdateTexts();
            UpdateEnabledStates();
            UpdateKeyTexts();
            UpdateAudioStatus();
        }
        finally
        {
            populating = false;
        }
        liveTimer.Start();
        UpdateLive();
    }

    private void PopulateDevices()
    {
        IReadOnlyList<AudioDeviceInfo> outputs = AudioDevices.GetOutputDevices();
        IReadOnlyList<AudioDeviceInfo> inputs = AudioDevices.GetInputDevices();
        AudioEngineConfig? active = runtime.Engine.Config;

        GameDeviceBox.ItemsSource = outputs;
        GameDeviceBox.SelectedItem = ById(outputs, S.GameOutputDeviceId) ?? ById(outputs, active?.GameOutputDeviceId);

        List<AudioDeviceInfo> monitors = outputs.Where(d => !d.IsVbCable).ToList();
        MonitorDeviceBox.ItemsSource = monitors;
        MonitorDeviceBox.SelectedItem = ById(monitors, S.MonitorDeviceId) ?? ById(monitors, active?.MonitorDeviceId)
                                        ?? ById(monitors, AudioDevices.GetDefaultOutput()?.Id);

        List<AudioDeviceInfo> mics = inputs.Where(d => !d.IsVbCable).ToList();
        MicDeviceBox.ItemsSource = mics;
        MicDeviceBox.SelectedItem = ById(mics, S.MicDeviceId) ?? ById(mics, active?.MicDeviceId)
                                    ?? ById(mics, AudioDevices.GetDefaultInput()?.Id);

        bool cableFound = AudioDevices.FindVbCableInput() is not null && AudioDevices.FindVbCableOutput() is not null;
        CableHint.Text = cableFound
            ? "✓ VB-Cable found. In Steam → Settings → Voice, set Voice Input Device to “CABLE Output (VB-Audio Virtual Cable)”."
            : "⚠ VB-Cable not found. Install it from vb-audio.com, reboot, then press “Refresh device list”.";
        CableHint.Foreground = Theme(cableFound ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }

    private static AudioDeviceInfo? ById(IEnumerable<AudioDeviceInfo> devices, string? id) =>
        id is null ? null : devices.FirstOrDefault(d => d.Id == id);

    private void UpdateTexts()
    {
        SoundVolumeText.Text = $"{SoundVolumeSlider.Value:0}%";
        MonitorVolumeText.Text = $"{MonitorVolumeSlider.Value:0}%";
        MicVolumeText.Text = $"{MicVolumeSlider.Value:0}%";
        ThresholdText.Text = $"{ThresholdSlider.Value:0} dB";
        AutoCancelText.Text = $"{AutoCancelSlider.Value:0.0} s";
        DeadzoneText.Text = $"{DeadzoneSlider.Value:0}";
        RadiusText.Text = $"{RadiusSlider.Value:0}";
        DwellText.Text = $"{DwellSlider.Value:0} ms";
        OverlaySizeText.Text = $"{OverlaySizeSlider.Value:0} px";
        OverlayOpacityText.Text = $"{OverlayOpacitySlider.Value:0}%";
    }

    private void UpdateEnabledStates()
    {
        bool micOn = S.MicMode != MicMode.Off;
        MicDeviceBox.IsEnabled = micOn;
        MicVolumeSlider.IsEnabled = micOn;
        PttKeyRow.IsEnabled = S.MicMode == MicMode.AppPushToTalk;
        PttKeyRow.Opacity = PttKeyRow.IsEnabled ? 1 : 0.5;
        VoiceRow.IsEnabled = S.MicMode == MicMode.VoiceActivation;
        VoiceRow.Opacity = VoiceRow.IsEnabled ? 1 : 0.5;

        AutoCancelRow.IsEnabled = S.AutoCancelEnabled;
        TimeoutCueBox.IsEnabled = S.AutoCancelEnabled;
        RestartOnHoverBox.IsEnabled = S.AutoCancelEnabled;

        OverlayOptions.IsEnabled = S.OverlayMode != OverlayMode.Off;
    }

    private void UpdateKeyTexts()
    {
        StopKeyText.Text = S.StopKey == VirtualKeys.None ? "not set" : VirtualKeys.GetName(S.StopKey);
        CancelKeyText.Text = S.CancelKey == VirtualKeys.None ? "not set" : VirtualKeys.GetName(S.CancelKey);
        PttKeyText.Text = S.PushToTalkKey == VirtualKeys.None ? "not set" : VirtualKeys.GetName(S.PushToTalkKey);
    }

    private void UpdateAudioStatus()
    {
        AudioStatus status = runtime.AudioStatus;
        AudioStatusText.Text = (status.IsOk ? "✓ " : "⚠ ") + status.Message;
        AudioStatusText.Foreground = Theme(status.IsOk ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
    }

    private void UpdateLive()
    {
        if (S.MicMode == MicMode.VoiceActivation)
        {
            float db = runtime.Engine.MicLevelDb;
            MicMeter.Value = Math.Clamp((db + 70) / 70 * 100, 0, 100);
            MicLevelText.Text = db <= -99 ? "Mic: silent" : db >= S.VoiceThresholdDb ? "Mic: open" : "Mic: closed";
        }
        else
        {
            MicMeter.Value = 0;
            MicLevelText.Text = "Mic level";
        }

        int background = runtime.BackgroundInputCount;
        BackgroundText.Text = background > 0
            ? $"✓ Hotkeys work while another window (e.g. CS2) has focus — {background} presses seen so far."
            : "Background hotkeys not confirmed yet: switch to another window or CS2 and press any key.";
        BackgroundText.Foreground = Theme(background > 0 ? "SystemFillColorSuccessBrush" : "TextFillColorPrimaryBrush");
    }

    // ───────────────────────── audio ─────────────────────────

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (populating)
        {
            return;
        }
        S.GameOutputDeviceId = (GameDeviceBox.SelectedItem as AudioDeviceInfo)?.Id ?? S.GameOutputDeviceId;
        S.MonitorDeviceId = (MonitorDeviceBox.SelectedItem as AudioDeviceInfo)?.Id ?? S.MonitorDeviceId;
        S.MicDeviceId = (MicDeviceBox.SelectedItem as AudioDeviceInfo)?.Id ?? S.MicDeviceId;
        runtime.RestartAudio();
        runtime.SaveSettings();
    }

    private void OnMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (populating)
        {
            return;
        }
        S.MonitorEnabled = MonitorEnabledBox.IsChecked == true;
        MonitorDeviceBox.IsEnabled = S.MonitorEnabled;
        runtime.RestartAudio();
        runtime.SaveSettings();
    }

    private void OnLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || !IsLoaded)
        {
            return;
        }
        S.SoundVolume = (float)(SoundVolumeSlider.Value / 100);
        S.MonitorVolume = (float)(MonitorVolumeSlider.Value / 100);
        S.MicVolume = (float)(MicVolumeSlider.Value / 100);
        runtime.ApplySettings();
        UpdateTexts();
        ScheduleSave();
    }

    private void OnAudioOptionChanged(object sender, RoutedEventArgs e)
    {
        if (populating)
        {
            return;
        }
        S.NormalizeLoudness = NormalizeBox.IsChecked == true;
        S.StopPreviousOnPlay = StopPreviousBox.IsChecked == true;
        runtime.SaveSettings();
    }

    private void OnRestartAudio(object sender, RoutedEventArgs e) => runtime.RestartAudio();

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        populating = true;
        try
        {
            PopulateDevices();
        }
        finally
        {
            populating = false;
        }
        runtime.RestartAudio();
    }

    // ───────────────────────── microphone ─────────────────────────

    private async void OnMicModeChanged(object sender, RoutedEventArgs e)
    {
        if (populating)
        {
            return;
        }
        S.MicMode = sender == ModeAppPtt ? MicMode.AppPushToTalk
            : sender == ModeVoice ? MicMode.VoiceActivation
            : sender == ModeOff ? MicMode.Off
            : MicMode.GamePushToTalk;
        runtime.ApplySettings();
        runtime.SaveSettings();
        UpdateEnabledStates();

        if (S.MicMode == MicMode.AppPushToTalk && S.PushToTalkKey == VirtualKeys.None)
        {
            await ChangePushToTalkKeyAsync();
        }
    }

    private async void OnChangePttKey(object sender, RoutedEventArgs e) => await ChangePushToTalkKeyAsync();

    private async Task ChangePushToTalkKeyAsync()
    {
        int? key = await KeyCaptureWindow.CaptureAsync(this, runtime, "Press the key you'll hold to talk.");
        if (key is not int vk)
        {
            return;
        }
        if (runtime.FindHotkeyConflict(vk, HotkeyRole.PushToTalk) is { } conflict)
        {
            MessageBox.Show(this, conflict, "Key already in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        S.PushToTalkKey = vk;
        runtime.SaveSettings();
        UpdateKeyTexts();
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || !IsLoaded)
        {
            return;
        }
        S.VoiceThresholdDb = (float)Math.Round(ThresholdSlider.Value);
        runtime.ApplySettings();
        UpdateTexts();
        ScheduleSave();
    }

    // ───────────────────────── wheel ─────────────────────────

    private void OnWheelOptionChanged(object sender, RoutedEventArgs e)
    {
        if (populating)
        {
            return;
        }
        S.AutoCancelEnabled = AutoCancelBox.IsChecked == true;
        S.TimeoutCue = TimeoutCueBox.IsChecked == true;
        S.RestartTimerOnHover = RestartOnHoverBox.IsChecked == true;
        runtime.ApplySettings();
        runtime.SaveSettings();
        UpdateEnabledStates();
    }

    private void OnWheelSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || !IsLoaded)
        {
            return;
        }

        S.AutoCancelSeconds = Math.Round(AutoCancelSlider.Value * 2) / 2;
        S.Deadzone = Math.Round(DeadzoneSlider.Value);
        double radius = Math.Round(RadiusSlider.Value);
        if (radius < S.Deadzone + 20)
        {
            radius = S.Deadzone + 20;
            populating = true;
            RadiusSlider.Value = radius;
            populating = false;
        }
        S.Radius = radius;
        S.FolderDwellMs = (int)(Math.Round(DwellSlider.Value / 10) * 10);

        runtime.ApplySettings();
        UpdateTexts();
        ScheduleSave();
    }

    private void OnResetFeel(object sender, RoutedEventArgs e)
    {
        populating = true;
        DeadzoneSlider.Value = 60;
        RadiusSlider.Value = 240;
        DwellSlider.Value = 350;
        populating = false;
        OnWheelSliderChanged(sender, new RoutedPropertyChangedEventArgs<double>(0, 0));
    }

    // ───────────────────────── overlay ─────────────────────────

    private void OnOverlayModeChanged(object sender, RoutedEventArgs e)
    {
        if (populating)
        {
            return;
        }
        S.OverlayMode = sender == OverlayApp ? OverlayMode.OverAppWindow
            : sender == OverlayOff ? OverlayMode.Off
            : OverlayMode.OnTopOfGame;
        runtime.SaveSettings();
        UpdateEnabledStates();
    }

    private void OnOverlaySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || !IsLoaded)
        {
            return;
        }
        S.OverlaySize = Math.Round(OverlaySizeSlider.Value);
        S.OverlayOpacity = Math.Round(OverlayOpacitySlider.Value) / 100;
        UpdateTexts();
        ScheduleSave();
    }

    private void OnOverlayOptionChanged(object sender, RoutedEventArgs e)
    {
        if (populating)
        {
            return;
        }
        S.ShowPlaybackTimer = PlaybackTimerBox.IsChecked == true;
        runtime.SaveSettings();
    }

    private void OnPreviewOverlay(object sender, RoutedEventArgs e) => overlay.ShowDemo();

    // ───────────────────────── hotkeys ─────────────────────────

    private async void OnChangeWheelHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WheelDefinition wheel)
        {
            return;
        }
        int? key = await KeyCaptureWindow.CaptureAsync(this, runtime, $"Press the key or mouse button that should open “{wheel.Name}”.");
        if (key is not int vk)
        {
            return;
        }
        if (runtime.FindHotkeyConflict(vk, HotkeyRole.Wheel, wheel.Id) is { } conflict)
        {
            MessageBox.Show(this, conflict, "Key already in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        wheel.Hotkey = vk;
        runtime.BoardChanged();
    }

    private async void OnChangeStopKey(object sender, RoutedEventArgs e)
    {
        int? key = await KeyCaptureWindow.CaptureAsync(this, runtime, "Press the key or mouse button that should stop every sound.");
        if (key is not int vk)
        {
            return;
        }
        if (runtime.FindHotkeyConflict(vk, HotkeyRole.StopAll) is { } conflict)
        {
            MessageBox.Show(this, conflict, "Key already in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        S.StopKey = vk;
        runtime.SaveSettings();
        UpdateKeyTexts();
    }

    private void OnClearStopKey(object sender, RoutedEventArgs e)
    {
        S.StopKey = VirtualKeys.None;
        runtime.SaveSettings();
        UpdateKeyTexts();
    }

    private async void OnChangeCancelKey(object sender, RoutedEventArgs e)
    {
        int? key = await KeyCaptureWindow.CaptureAsync(this, runtime, "Press the key or mouse button that should close an open wheel without playing anything.");
        if (key is not int vk)
        {
            return;
        }
        if (runtime.FindHotkeyConflict(vk, HotkeyRole.CancelWheel) is { } conflict)
        {
            MessageBox.Show(this, conflict, "Key already in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        S.CancelKey = vk;
        runtime.ApplySettings();
        runtime.SaveSettings();
        UpdateKeyTexts();
    }

    private void OnClearCancelKey(object sender, RoutedEventArgs e)
    {
        S.CancelKey = VirtualKeys.None;
        runtime.ApplySettings();
        runtime.SaveSettings();
        UpdateKeyTexts();
    }

    // ───────────────────────── backup ─────────────────────────

    private void OnSaveConfiguration(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save configuration",
            Filter = "Soundboard backup (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"Jrik's Soundboard backup {DateTime.Now:yyyy-MM-dd}.zip",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            BackupManifest saved = ConfigBackup.Save(dialog.FileName, S, runtime.Board, IncludeAudioBox.IsChecked == true, runtime.AppVersion);
            double megabytes = new FileInfo(dialog.FileName).Length / (1024.0 * 1024.0);
            BackupStatusText.Text = $"✓ Saved {saved.SoundCount} sound(s) and {saved.WheelCount} wheel(s)"
                                    + (saved.IncludesAudio ? $" with {saved.AudioFiles} audio file(s)" : string.Empty)
                                    + $" ({megabytes:0.0} MB).";
            BackupStatusText.Foreground = Theme("SystemFillColorSuccessBrush");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the configuration:\n{ex.Message}", "Save configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnLoadConfiguration(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load configuration",
            Filter = "Soundboard backup (*.zip)|*.zip|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BackupManifest manifest;
        try
        {
            manifest = ConfigBackup.ReadManifest(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string summary = $"{manifest.SoundCount} sound(s), {manifest.WheelCount} wheel(s)"
                         + (manifest.IncludesAudio ? ", audio files included" : ", without audio files");
        if (MessageBox.Show(this,
                $"Replace your current library, wheels and settings with this backup?\n\nSaved {manifest.CreatedUtc.ToLocalTime():g}: {summary}.\n\nYour current setup is saved as an automatic backup first, so you can always go back.",
                "Load configuration", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            runtime.CreateSafetyBackup("before-load");
            LoadedBackup loaded = ConfigBackup.Load(dialog.FileName, runtime.RestoredAudioFolder);
            runtime.ReplaceConfiguration(loaded.Settings, loaded.Board);
            LoadValues();
            ConfigurationLoaded?.Invoke();

            string message = $"✓ Loaded {loaded.Board.Sounds.Count} sound(s) and {loaded.Board.Wheels.Count} wheel(s).";
            if (loaded.SoundsRestoredFromBackup > 0)
            {
                message += $" {loaded.SoundsRestoredFromBackup} audio file(s) were restored from the backup into “{runtime.RestoredAudioFolder}”.";
            }
            if (loaded.MissingSounds.Count > 0)
            {
                message += $"\n⚠ {loaded.MissingSounds.Count} sound file(s) couldn't be found: {string.Join(", ", loaded.MissingSounds.Take(8))}"
                           + (loaded.MissingSounds.Count > 8 ? "…" : string.Empty);
            }
            BackupStatusText.Text = message;
            BackupStatusText.Foreground = Theme(loaded.MissingSounds.Count > 0 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't load the configuration:\n{ex.Message}", "Load configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnOpenBackupsFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(runtime.BackupFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{runtime.BackupFolder}\"") { UseShellExecute = true });
    }

    // ───────────────────────── diagnostics ─────────────────────────

    private void OnTestTone(object sender, RoutedEventArgs e)
    {
        try
        {
            runtime.PlayTestTone();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't play the test tone: {ex.Message}", "Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
    }

    private static System.Windows.Media.Brush Theme(string key) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
}
