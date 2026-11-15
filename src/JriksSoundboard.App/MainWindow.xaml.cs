using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Runtime;
using JriksSoundboard.Core.Wheel;
using Microsoft.Win32;

namespace JriksSoundboard.App;

/// <summary>Library on the left, wheel editor on the right. Everything else lives in Settings.</summary>
public partial class MainWindow : Window
{
    private readonly SoundboardRuntime runtime;
    private readonly LogStore log;
    private readonly string? snapshotDir;
    private readonly OverlayWindow overlay;
    private readonly DispatcherTimer statusTimer;
    private readonly DispatcherTimer saveDebounce;
    private readonly ICollectionView libraryView;

    private SettingsWindow? settingsWindow;
    private WheelDefinition? currentWheel;
    private WheelSlot? openFolder; // folder slot being edited; null = the wheel itself
    private int? selectedSlot;
    private int? hoveredSlot;
    private SoundItem? selectedSound;
    private SoundItem? dragCandidate;
    private Point dragStart;
    private bool populating;

    public MainWindow(SoundboardRuntime runtime, LogStore log, string? snapshotDir)
    {
        this.runtime = runtime;
        this.log = log;
        this.snapshotDir = snapshotDir;
        InitializeComponent();

        LibraryList.ItemsSource = runtime.Board.Sounds;
        libraryView = CollectionViewSource.GetDefaultView(runtime.Board.Sounds);
        libraryView.Filter = MatchesSearch;
        runtime.Board.Sounds.CollectionChanged += (_, _) => UpdateLibraryHeader();

        WheelTabs.ItemsSource = runtime.Board.Wheels;
        SliceCountBox.ItemsSource = WheelPage.AllowedSliceCounts;

        EditorWheel.IsEditable = true;
        EditorWheel.SliceClicked += OnSliceClicked;
        EditorWheel.SliceDoubleClicked += OnSliceDoubleClicked;
        EditorWheel.SoundDropped += OnSoundDropped;
        EditorWheel.SliceMoved += OnSliceMoved;
        EditorWheel.SliceHovered += index =>
        {
            hoveredSlot = index;
            UpdateSliceName();
        };

        overlay = new OverlayWindow(runtime, this);
        runtime.AudioStatusChanged += _ => Dispatcher.BeginInvoke(UpdateStatus);

        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        statusTimer.Tick += (_, _) => UpdateNowPlaying();
        saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveDebounce.Tick += (_, _) =>
        {
            saveDebounce.Stop();
            runtime.BoardChanged();
        };

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public OverlayWindow Overlay => overlay;

    public SoundboardRuntime Runtime => runtime;

    /// <summary>
    /// Path for display: hides the user-profile folder (so screenshots don't reveal your Windows
    /// user name) and, in snapshot mode, shows demo files relative to the snapshot folder.
    /// </summary>
    private string DisplayPath(string path)
    {
        if (snapshotDir is not null && path.StartsWith(snapshotDir, StringComparison.OrdinalIgnoreCase))
        {
            return System.IO.Path.GetRelativePath(snapshotDir, path);
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[profile.Length..]
            : path;
    }

    // ───────────────────────── lifecycle ─────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        runtime.IsAppInForeground = NativeWin32.IsOwnProcessForeground;
        runtime.Start();
        overlay.Initialize();

        WheelTabs.SelectedIndex = runtime.Board.Wheels.Count > 0 ? 0 : -1;
        UpdateLibraryHeader();
        UpdateSoundDetails();
        UpdateStatus();
        UpdateNowPlaying();
        statusTimer.Start();

        if (snapshotDir is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => SnapshotRunner.Run(this, snapshotDir));
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (saveDebounce.IsEnabled)
        {
            saveDebounce.Stop();
            runtime.BoardChanged();
        }
        statusTimer.Stop();
        settingsWindow?.Close();
        overlay.Close();
    }

    private void ScheduleSave()
    {
        saveDebounce.Stop();
        saveDebounce.Start();
    }

    // ───────────────────────── top bar ─────────────────────────

    private void UpdateStatus()
    {
        AudioStatus status = runtime.AudioStatus;
        string text = status.Message;
        if (runtime.InputError is { } error)
        {
            text += " · Hotkeys unavailable: " + error;
        }
        StatusText.Text = text;
        bool ok = status.IsOk && runtime.InputError is null;
        StatusDot.Fill = ThemeBrush(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush", ok ? Brushes.LimeGreen : Brushes.Orange);
    }

    private void UpdateNowPlaying()
    {
        SoundPlayback? playback = runtime.Engine.LatestPlayback;
        if (playback is { IsFinished: false })
        {
            NowPlayingText.Text = $"▶  {runtime.LastPlayedName ?? playback.Sound.Name}  —  {playback.Remaining.TotalSeconds:0.0}s left";
            NowPlayingProgress.Value = playback.Progress * 100;
            return;
        }

        NowPlayingProgress.Value = 0;
        List<string> bound = runtime.Board.Wheels
            .Where(w => w.Hotkey != 0)
            .Select(w => $"{w.HotkeyName} → {w.Name}")
            .ToList();
        NowPlayingText.Text = bound.Count == 0
            ? "No wheel has a hotkey yet — set one with “Change…” above."
            : "Ready · in game hold " + string.Join("  ·  ", bound) + ", flick toward a sound, release";
    }

    private void OnStopAll(object sender, RoutedEventArgs e) => runtime.StopAll();

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    public SettingsWindow OpenSettings()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return settingsWindow;
        }

        settingsWindow = new SettingsWindow(runtime, log, overlay) { Owner = this };
        settingsWindow.ConfigurationLoaded += OnConfigurationLoaded;
        settingsWindow.Closed += (_, _) =>
        {
            settingsWindow = null;
            RefreshEditor();
            UpdateStatus();
        };
        settingsWindow.Show();
        return settingsWindow;
    }

    /// <summary>A backup was loaded: everything on screen now points at replaced content.</summary>
    private void OnConfigurationLoaded()
    {
        currentWheel = null;
        openFolder = null;
        selectedSlot = null;
        hoveredSlot = null;
        selectedSound = null;
        LibraryList.SelectedItem = null;
        WheelTabs.SelectedIndex = -1;
        WheelTabs.SelectedIndex = runtime.Board.Wheels.Count > 0 ? 0 : -1;
        currentWheel ??= WheelTabs.SelectedItem as WheelDefinition;
        UpdateLibraryHeader();
        UpdateSoundDetails();
        RefreshEditor();
        UpdateStatus();
    }

    // ───────────────────────── library ─────────────────────────

    private bool MatchesSearch(object item)
    {
        string query = SearchBox?.Text.Trim() ?? string.Empty;
        return item is SoundItem sound && (query.Length == 0 || sound.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        libraryView.Refresh();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLibraryHeader()
    {
        int count = runtime.Board.Sounds.Count;
        LibraryCount.Text = count == 1 ? "1 sound" : $"{count} sounds";
        LibraryEmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAddSounds(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add sounds",
            Multiselect = true,
            Filter = "Audio files|*.mp3;*.wav;*.ogg;*.oga;*.flac;*.m4a;*.aac;*.wma;*.aif;*.aiff|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportAsync(dialog.FileNames);
        }
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add every sound in a folder (subfolders included)", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportAsync(dialog.FolderNames);
        }
    }

    private void OnLibraryDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnLibraryDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            await ImportAsync(paths);
        }
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(runtime.Board.Sounds.Select(s => s.FilePath), StringComparer.OrdinalIgnoreCase);
        List<string> files = ExpandToAudioFiles(paths).Where(known.Add).ToList();
        if (files.Count == 0)
        {
            log.Add("Nothing new to add (no supported audio files, or they're already in the library).");
            return;
        }

        LibraryCount.Text = $"Adding {files.Count}…";
        var probed = await Task.Run(() => files.Select(path =>
        {
            bool ok = CachedSound.TryProbe(path, out TimeSpan duration, out string? error);
            return (Path: path, Ok: ok, Duration: duration, Error: error);
        }).ToList());

        var failed = new List<string>();
        foreach (var file in probed)
        {
            if (!file.Ok)
            {
                failed.Add($"{System.IO.Path.GetFileName(file.Path)} — {file.Error}");
                continue;
            }
            var item = new SoundItem
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(file.Path),
                FilePath = file.Path,
                DurationSeconds = file.Duration.TotalSeconds,
            };
            item.RefreshFileState();
            runtime.Board.Sounds.Add(item);
        }

        int added = probed.Count - failed.Count;
        runtime.BoardChanged();
        UpdateLibraryHeader();
        log.Add($"Added {added} sound(s)" + (failed.Count > 0 ? $", skipped {failed.Count}" : string.Empty));

        if (failed.Count > 0)
        {
            string list = string.Join("\n", failed.Take(12)) + (failed.Count > 12 ? $"\n…and {failed.Count - 12} more" : string.Empty);
            MessageBox.Show(this, $"Added {added} sound(s). These couldn't be read:\n\n{list}", "Some files were skipped",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private List<string> ExpandToAudioFiles(IEnumerable<string> paths)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var result = new List<string>();
        foreach (string raw in paths)
        {
            string path = System.IO.Path.GetFullPath(raw);
            if (Directory.Exists(path))
            {
                result.AddRange(Directory.EnumerateFiles(path, "*", options).Where(CachedSound.IsSupported));
            }
            else if (File.Exists(path) && CachedSound.IsSupported(path))
            {
                result.Add(path);
            }
        }
        return result.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void OnPreviewSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SoundItem sound)
        {
            runtime.PreviewSound(sound);
        }
    }

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedSound = LibraryList.SelectedItem as SoundItem;
        UpdateSoundDetails();
    }

    private void UpdateSoundDetails()
    {
        populating = true;
        try
        {
            SoundDetails.IsEnabled = selectedSound is not null;
            SoundNameBox.Text = selectedSound?.Name ?? string.Empty;
            SoundVolumeSlider.Value = (selectedSound?.Volume ?? 1f) * 100;
            SoundVolumeText.Text = $"{SoundVolumeSlider.Value:0}%";
            SoundPathText.Text = selectedSound is null ? "Select a sound to rename it, change its volume or remove it." : DisplayPath(selectedSound.FilePath);
            SoundPathText.ToolTip = selectedSound?.FilePath;
        }
        finally
        {
            populating = false;
        }
    }

    private void CommitSoundName()
    {
        if (selectedSound is null)
        {
            return;
        }
        string name = SoundNameBox.Text.Trim();
        if (name.Length > 0 && name != selectedSound.Name)
        {
            selectedSound.Name = name;
            runtime.BoardChanged();
            RefreshEditor();
        }
        else
        {
            SoundNameBox.Text = selectedSound.Name;
        }
    }

    private void OnSoundNameLostFocus(object sender, RoutedEventArgs e) => CommitSoundName();

    private void OnSoundNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSoundName();
            Keyboard.ClearFocus();
        }
    }

    private void OnSoundVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || !IsLoaded || selectedSound is null)
        {
            return;
        }
        selectedSound.Volume = (float)(SoundVolumeSlider.Value / 100);
        SoundVolumeText.Text = $"{SoundVolumeSlider.Value:0}%";
        ScheduleSave();
    }

    private void OnPlaySelectedInGame(object sender, RoutedEventArgs e)
    {
        if (selectedSound is not null)
        {
            runtime.PlaySound(selectedSound);
        }
    }

    private void OnShowFile(object sender, RoutedEventArgs e)
    {
        if (selectedSound is null)
        {
            return;
        }
        if (!File.Exists(selectedSound.FilePath))
        {
            MessageBox.Show(this, $"The file isn't there any more:\n{selectedSound.FilePath}", "File missing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selectedSound.FilePath}\"") { UseShellExecute = true });
    }

    private void OnRemoveSound(object sender, RoutedEventArgs e) => RemoveSelectedSound();

    private void RemoveSelectedSound()
    {
        if (selectedSound is not { } sound)
        {
            return;
        }
        int uses = runtime.Board.CountUses(sound.Id);
        if (uses > 0 && MessageBox.Show(this,
                $"“{sound.Name}” is on {uses} wheel slot(s). Remove it from the library and those slots?\n\n(The file itself is not deleted.)",
                "Remove sound", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        runtime.Board.RemoveSound(sound);
        runtime.BoardChanged();
        RefreshEditor();
        log.Add($"Removed “{sound.Name}” from the library");
    }

    private void OnLibraryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelectedSound();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && selectedSound is not null)
        {
            runtime.PreviewSound(selectedSound);
            e.Handled = true;
        }
    }

    private void OnLibraryPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragCandidate = null;
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<Button>(source) is not null)
        {
            return;
        }
        if (FindAncestor<ListBoxItem>(source)?.DataContext is SoundItem sound)
        {
            dragCandidate = sound;
            dragStart = e.GetPosition(LibraryList);
        }
    }

    private void OnLibraryMouseMove(object sender, MouseEventArgs e)
    {
        if (dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        Vector moved = e.GetPosition(LibraryList) - dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        SoundItem sound = dragCandidate;
        dragCandidate = null;
        DragDrop.DoDragDrop(LibraryList, new DataObject(WheelView.SoundDragFormat, sound.Id.ToString()), DragDropEffects.Copy);
    }

    private void OnLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<Button>(source) is null && FindAncestor<ListBoxItem>(source)?.DataContext is SoundItem sound)
        {
            AddSoundToWheel(sound);
        }
    }

    /// <summary>Puts a sound into the selected slot (or the first empty one).</summary>
    private void AddSoundToWheel(SoundItem sound)
    {
        if (CurrentPage is not { } page)
        {
            return;
        }

        int? target = selectedSlot is int i && i < page.Slots.Count ? i : null;
        if (target is int t && page.Slots[t].Kind == SlotKind.Folder && page.Slots[t].Folder is { } folder)
        {
            AddIntoFolder(page.Slots[t], folder, sound.Id);
            return;
        }

        target ??= FirstEmpty(page);
        if (target is not int index)
        {
            MessageBox.Show(this, "This wheel is full. Select a slot to replace, or add more slices.", "No empty slot",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        page.Slots[index].AssignSound(sound.Id);
        selectedSlot = index;
        runtime.BoardChanged();
        RefreshEditor();
    }

    private void AddIntoFolder(WheelSlot folderSlot, WheelPage folder, Guid soundId)
    {
        if (FirstEmpty(folder) is not int index)
        {
            MessageBox.Show(this, $"The folder “{folderSlot.Label}” is full.", "Folder full", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        folder.Slots[index].AssignSound(soundId);
        runtime.BoardChanged();
        RefreshEditor();
        log.Add($"Added to folder “{folderSlot.Label}” ({WheelSelector.DescribeSlice(index, folder.SliceCount)})");
    }

    private static int? FirstEmpty(WheelPage page)
    {
        for (int i = 0; i < page.Slots.Count; i++)
        {
            if (page.Slots[i].IsEmpty)
            {
                return i;
            }
        }
        return null;
    }

    // ───────────────────────── wheels ─────────────────────────

    private WheelPage? CurrentPage => openFolder?.Folder ?? currentWheel?.Root;

    private void OnWheelTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WheelTabs.SelectedItem is WheelDefinition wheel)
        {
            currentWheel = wheel;
            openFolder = null;
            selectedSlot = null;
            RefreshEditor();
        }
    }

    private async void OnNewWheel(object sender, RoutedEventArgs e)
    {
        var wheel = new WheelDefinition { Name = $"Wheel {runtime.Board.Wheels.Count + 1}" };
        runtime.Board.Wheels.Add(wheel);
        WheelTabs.SelectedItem = wheel;
        runtime.BoardChanged();
        await ChangeHotkeyAsync(wheel);
    }

    private void OnDeleteWheel(object sender, RoutedEventArgs e)
    {
        if (currentWheel is not { } wheel)
        {
            return;
        }
        if (runtime.Board.Wheels.Count <= 1)
        {
            MessageBox.Show(this, "You need at least one wheel.", "Delete wheel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Delete the wheel “{wheel.Name}”? The sounds stay in your library.", "Delete wheel",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        runtime.Board.Wheels.Remove(wheel);
        WheelTabs.SelectedIndex = 0;
        runtime.BoardChanged();
    }

    private void CommitWheelName()
    {
        if (currentWheel is null)
        {
            return;
        }
        string name = WheelNameBox.Text.Trim();
        if (name.Length > 0 && name != currentWheel.Name)
        {
            currentWheel.Name = name;
            runtime.BoardChanged();
        }
        RefreshEditor();
    }

    private void OnWheelNameLostFocus(object sender, RoutedEventArgs e) => CommitWheelName();

    private void OnWheelNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitWheelName();
            Keyboard.ClearFocus();
        }
    }

    private void OnSliceCountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (populating || CurrentPage is not { } page || SliceCountBox.SelectedItem is not int count || count == page.SliceCount)
        {
            return;
        }

        int lost = page.CountSlotsLostByResize(count);
        if (lost > 0 && MessageBox.Show(this,
                $"{lost} filled slot(s) won't fit on a {count}-slice wheel and will be cleared. Continue?",
                "Fewer slices", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            RefreshEditor();
            return;
        }

        page.Resize(count);
        selectedSlot = null;
        runtime.BoardChanged();
        RefreshEditor();
    }

    private async void OnChangeWheelHotkey(object sender, RoutedEventArgs e)
    {
        if (currentWheel is not null)
        {
            await ChangeHotkeyAsync(currentWheel);
        }
    }

    private async Task ChangeHotkeyAsync(WheelDefinition wheel)
    {
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
        RefreshEditor();
        log.Add($"“{wheel.Name}” now opens with {wheel.HotkeyName}");
    }

    // ───────────────────────── editor ─────────────────────────

    private void RefreshEditor()
    {
        populating = true;
        try
        {
            WheelsPanel.IsEnabled = currentWheel is not null;
            if (currentWheel is null || CurrentPage is not { } page)
            {
                EditorWheel.Slots = [];
                return;
            }

            if (selectedSlot is int s && s >= page.Slots.Count)
            {
                selectedSlot = null;
            }

            WheelNameBox.Text = currentWheel.Name;
            WheelHotkeyText.Text = currentWheel.HotkeyName;
            SliceCountBox.SelectedItem = page.SliceCount;
            BreadcrumbText.Text = openFolder is null ? currentWheel.Name : $"{currentWheel.Name}   ›   {openFolder.Label}";
            BackButton.Visibility = openFolder is null ? Visibility.Collapsed : Visibility.Visible;

            EditorWheel.Slots = page.Slots.Select(ToVisual).ToList();
            EditorWheel.CenterText = openFolder?.Label ?? currentWheel.Name;
            EditorWheel.HighlightedSlot = selectedSlot;
            RefreshSlotPanel(page);
        }
        finally
        {
            populating = false;
        }
        UpdateSliceName();
    }

    /// <summary>Full name of the slice under the mouse (or the selected one) above the wheel.</summary>
    private void UpdateSliceName()
    {
        int? index = hoveredSlot ?? selectedSlot;
        if (CurrentPage is not { } page || index is not int i || i >= page.Slots.Count)
        {
            SliceNameText.Text = string.Empty;
            return;
        }

        WheelSlot slot = page.Slots[i];
        SliceNameText.Text = slot.Kind switch
        {
            SlotKind.Sound => slot.SoundId is Guid id && runtime.Board.FindSound(id) is { } sound ? slot.Label ?? sound.Name : string.Empty,
            SlotKind.Folder => $"{slot.Label} (folder)",
            _ => "Empty slice",
        };
        SliceNameText.Opacity = hoveredSlot is null ? 0.7 : 1.0;
    }

    private WheelSlotVisual ToVisual(WheelSlot slot)
    {
        if (slot.Kind == SlotKind.Sound && slot.SoundId is Guid id && runtime.Board.FindSound(id) is { } sound)
        {
            return new WheelSlotVisual(slot.Label ?? sound.Name, SlotKind.Sound, sound.IsMissing);
        }
        if (slot.Kind == SlotKind.Folder)
        {
            return new WheelSlotVisual(slot.Label ?? "Folder", SlotKind.Folder);
        }
        return WheelSlotVisual.Empty;
    }

    private void RefreshSlotPanel(WheelPage page)
    {
        SlotSoundActions.Visibility = Visibility.Collapsed;
        SlotFolderActions.Visibility = Visibility.Collapsed;
        SlotEmptyActions.Visibility = Visibility.Collapsed;

        if (selectedSlot is not int index)
        {
            SlotTitle.Text = "No slice selected";
            SlotContent.Text = "Click a slice to select it, or drag a sound from the library onto one.";
            return;
        }

        WheelSlot slot = page.Slots[index];
        SlotTitle.Text = $"{WheelSelector.DescribeSlice(index, page.SliceCount)}";
        switch (slot.Kind)
        {
            case SlotKind.Sound:
                SoundItem? sound = slot.SoundId is Guid id ? runtime.Board.FindSound(id) : null;
                SlotContent.Text = sound is null ? "(unknown sound)"
                    : sound.IsMissing ? $"{sound.Name}\n⚠ The file is missing." : sound.Name;
                SlotSoundActions.Visibility = Visibility.Visible;
                break;

            case SlotKind.Folder:
                int count = slot.Folder?.Slots.Count(s => !s.IsEmpty) ?? 0;
                SlotContent.Text = $"Folder with {count} sound(s) on a {slot.Folder?.SliceCount ?? 0}-slice wheel.";
                FolderNameBox.Text = slot.Label ?? string.Empty;
                SlotFolderActions.Visibility = Visibility.Visible;
                break;

            default:
                SlotContent.Text = openFolder is null
                    ? "Empty. Drag a sound here, or turn this slice into a folder."
                    : "Empty. Drag a sound here.";
                MakeFolderButton.Visibility = openFolder is null ? Visibility.Visible : Visibility.Collapsed;
                SlotEmptyActions.Visibility = Visibility.Visible;
                break;
        }
    }

    private WheelSlot? SelectedSlotModel =>
        CurrentPage is { } page && selectedSlot is int i && i < page.Slots.Count ? page.Slots[i] : null;

    private void OnSliceClicked(int index)
    {
        selectedSlot = index;
        EditorWheel.HighlightedSlot = index;
        if (CurrentPage is { } page)
        {
            RefreshSlotPanel(page);
        }
        UpdateSliceName();
    }

    private void OnSliceDoubleClicked(int index)
    {
        if (CurrentPage is not { } page || index >= page.Slots.Count)
        {
            return;
        }
        WheelSlot slot = page.Slots[index];
        if (slot.Kind == SlotKind.Folder && openFolder is null)
        {
            OpenFolder(slot);
        }
        else if (slot.Kind == SlotKind.Sound && slot.SoundId is Guid id && runtime.Board.FindSound(id) is { } sound)
        {
            runtime.PreviewSound(sound);
        }
    }

    private void OnSoundDropped(int index, Guid soundId)
    {
        if (CurrentPage is not { } page || index >= page.Slots.Count || runtime.Board.FindSound(soundId) is null)
        {
            return;
        }
        WheelSlot slot = page.Slots[index];
        if (slot.Kind == SlotKind.Folder && slot.Folder is { } folder)
        {
            AddIntoFolder(slot, folder, soundId);
            return;
        }
        slot.AssignSound(soundId);
        selectedSlot = index;
        runtime.BoardChanged();
        RefreshEditor();
    }

    private void OnSliceMoved(int from, int to)
    {
        if (CurrentPage is not { } page || from >= page.Slots.Count || to >= page.Slots.Count)
        {
            return;
        }
        WheelSlot.Swap(page.Slots[from], page.Slots[to]);
        selectedSlot = to;
        runtime.BoardChanged();
        RefreshEditor();
    }

    private void OnSlotPreview(object sender, RoutedEventArgs e)
    {
        if (SelectedSlotModel is { Kind: SlotKind.Sound, SoundId: Guid id } && runtime.Board.FindSound(id) is { } sound)
        {
            runtime.PreviewSound(sound);
        }
    }

    private void OnSlotClear(object sender, RoutedEventArgs e)
    {
        SelectedSlotModel?.Clear();
        runtime.BoardChanged();
        RefreshEditor();
    }

    private void OnMakeFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedSlotModel is not { IsEmpty: true } slot || openFolder is not null)
        {
            return;
        }
        int number = runtime.Board.Wheels.SelectMany(w => w.Root.Slots).Count(s => s.Kind == SlotKind.Folder) + 1;
        slot.MakeFolder($"Folder {number}");
        runtime.BoardChanged();
        RefreshEditor();
        FolderNameBox.Focus();
        FolderNameBox.SelectAll();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedSlotModel is { Kind: SlotKind.Folder } slot)
        {
            OpenFolder(slot);
        }
    }

    private void OpenFolder(WheelSlot folderSlot)
    {
        openFolder = folderSlot;
        selectedSlot = null;
        RefreshEditor();
    }

    private void OnFolderBack(object sender, RoutedEventArgs e)
    {
        if (openFolder is null || currentWheel is null)
        {
            return;
        }
        int index = currentWheel.Root.Slots.IndexOf(openFolder);
        openFolder = null;
        selectedSlot = index >= 0 ? index : null;
        RefreshEditor();
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedSlotModel is not { Kind: SlotKind.Folder } slot)
        {
            return;
        }
        int count = slot.Folder?.Slots.Count(s => !s.IsEmpty) ?? 0;
        if (count > 0 && MessageBox.Show(this,
                $"Remove the folder “{slot.Label}” and its {count} slot(s)? The sounds stay in your library.",
                "Remove folder", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        slot.Clear();
        runtime.BoardChanged();
        RefreshEditor();
    }

    private void CommitFolderName()
    {
        if (SelectedSlotModel is not { Kind: SlotKind.Folder } slot)
        {
            return;
        }
        string name = FolderNameBox.Text.Trim();
        if (name.Length > 0 && name != slot.Label)
        {
            slot.Label = name;
            runtime.BoardChanged();
            RefreshEditor();
        }
    }

    private void OnFolderNameLostFocus(object sender, RoutedEventArgs e) => CommitFolderName();

    private void OnFolderNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFolderName();
            Keyboard.ClearFocus();
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Puts the window into a representative state for UI snapshots.</summary>
    public void PrepareSnapshot()
    {
        WheelTabs.SelectedIndex = 0;
        LibraryList.SelectedIndex = 1;
        selectedSlot = 2;
        RefreshEditor();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return node as T;
    }

    private static Brush ThemeBrush(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;
}
