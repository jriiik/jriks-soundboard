using System.IO.Compression;
using System.Text.Json;
using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Board;

namespace JriksSoundboard.Core.Settings;

/// <summary>Describes a backup file (stored as manifest.json inside it).</summary>
public sealed record BackupManifest(
    string Format,
    int Version,
    DateTime CreatedUtc,
    string? AppVersion,
    bool IncludesAudio,
    int SoundCount,
    int WheelCount,
    int AudioFiles);

/// <summary>A backup read back from disk, ready to replace the current configuration.</summary>
public sealed record LoadedBackup(
    AppSettings Settings,
    SoundBoard Board,
    BackupManifest Manifest,
    int SoundsRestoredFromBackup,
    IReadOnlyList<string> MissingSounds);

/// <summary>
/// Saves / loads the whole setup — library, wheels, folders, slice positions, hotkeys and all
/// settings — as one .zip file, optionally with the audio files inside.
/// <para>
/// Paths under the user's profile are stored as <c>%USERPROFILE%\…</c>, so a backup never
/// contains the Windows user name and still resolves on the same PC. On load, sounds whose
/// original file still exists keep using it; missing ones are restored from the backup copy.
/// </para>
/// </summary>
public static class ConfigBackup
{
    public const string FormatName = "JriksSoundboardBackup";
    public const int FormatVersion = 1;
    public const string AutomaticPrefix = "auto-";

    private const string ManifestEntry = "manifest.json";
    private const string SettingsEntry = "settings.json";
    private const string BoardEntry = "board.json";
    private const string SoundsPrefix = "sounds/";
    private const string ProfileToken = "%USERPROFILE%";
    private const long MaxAudioEntryBytes = 300L * 1024 * 1024; // refuse absurd entries (zip bombs)

    private static readonly HashSet<string> CompressedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".ogg", ".oga", ".m4a", ".aac", ".wma", ".flac",
    };

    /// <summary>Writes a backup. The file is written to a temp name first, so a failed save never leaves a half-written backup.</summary>
    public static BackupManifest Save(string zipPath, AppSettings settings, SoundBoard board, bool includeAudio, string? appVersion = null)
    {
        SoundBoard portable = Clone(board);
        string fullPath = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + ".tmp";
        File.Delete(temp);

        BackupManifest manifest;
        using (ZipArchive zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            int audioFiles = 0;
            foreach (SoundItem sound in portable.Sounds)
            {
                if (includeAudio && File.Exists(sound.FilePath))
                {
                    string extension = Path.GetExtension(sound.FilePath);
                    CompressionLevel level = CompressedFormats.Contains(extension) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    zip.CreateEntryFromFile(sound.FilePath, SoundsPrefix + sound.Id.ToString("N") + extension, level);
                    audioFiles++;
                }
                sound.FilePath = ToPortablePath(sound.FilePath);
            }

            manifest = new BackupManifest(FormatName, FormatVersion, DateTime.UtcNow, appVersion, includeAudio,
                portable.Sounds.Count, portable.Wheels.Count, audioFiles);
            WriteJson(zip, ManifestEntry, manifest);
            WriteJson(zip, SettingsEntry, settings);
            WriteJson(zip, BoardEntry, portable);
        }

        File.Move(temp, fullPath, overwrite: true);
        return manifest;
    }

    /// <summary>
    /// Reads a backup. Sounds missing from their original location are extracted into
    /// <paramref name="audioFolder"/> (only supported audio types are ever extracted).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a backup, or made by a newer app version.</exception>
    public static LoadedBackup Load(string zipPath, string audioFolder)
    {
        using ZipArchive zip = OpenArchive(zipPath);

        BackupManifest manifest = ReadJson<BackupManifest>(zip, ManifestEntry);
        if (manifest.Format != FormatName)
        {
            throw new InvalidDataException("This file isn't a Jrik's Soundboard backup.");
        }
        if (manifest.Version > FormatVersion)
        {
            throw new InvalidDataException("This backup was made by a newer version of Jrik's Soundboard. Update the app to load it.");
        }

        AppSettings settings = ReadJson<AppSettings>(zip, SettingsEntry);
        SoundBoard board = ReadJson<SoundBoard>(zip, BoardEntry);

        var missing = new List<string>();
        int restored = 0;
        foreach (SoundItem sound in board.Sounds)
        {
            string original = Environment.ExpandEnvironmentVariables(sound.FilePath ?? string.Empty);
            if (original.Length > 0 && File.Exists(original))
            {
                sound.FilePath = original;
                continue;
            }

            ZipArchiveEntry? entry = FindAudioEntry(zip, sound.Id);
            if (entry is not null)
            {
                Directory.CreateDirectory(audioFolder);
                string target = UniquePath(audioFolder, SafeFileName(sound.Name), Path.GetExtension(entry.Name));
                entry.ExtractToFile(target);
                sound.FilePath = target;
                restored++;
            }
            else
            {
                sound.FilePath = original;
                missing.Add(sound.Name);
            }
        }

        board.Normalize();
        return new LoadedBackup(settings, board, manifest, restored, missing);
    }

    /// <summary>Reads just the manifest (to describe a backup before loading it).</summary>
    public static BackupManifest ReadManifest(string zipPath)
    {
        using ZipArchive zip = OpenArchive(zipPath);
        return ReadJson<BackupManifest>(zip, ManifestEntry);
    }

    /// <summary>Saves a safety copy (without audio) as auto-&lt;timestamp&gt;-&lt;reason&gt;.zip and keeps only the newest <paramref name="keep"/>.</summary>
    public static string SaveAutomatic(string folder, AppSettings settings, SoundBoard board, string reason, int keep = 10, string? appVersion = null)
    {
        string path = Path.Combine(folder, $"{AutomaticPrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{reason}.zip");
        Save(path, settings, board, includeAudio: false, appVersion);
        foreach (FileInfo old in AutomaticBackups(folder).Skip(keep))
        {
            old.Delete();
        }
        return path;
    }

    /// <summary>Automatic backups in a folder, newest first.</summary>
    public static IReadOnlyList<FileInfo> AutomaticBackups(string folder) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles(AutomaticPrefix + "*.zip").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Replaces the user-profile part of a path with %USERPROFILE% (no user name in backups).</summary>
    public static string ToPortablePath(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? ProfileToken + path[profile.Length..]
            : path;
    }

    private static ZipArchive OpenArchive(string zipPath)
    {
        try
        {
            return ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This file isn't a Jrik's Soundboard backup (it's not a valid .zip).");
        }
    }

    private static ZipArchiveEntry? FindAudioEntry(ZipArchive zip, Guid soundId)
    {
        string prefix = SoundsPrefix + soundId.ToString("N");
        return zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && CachedSound.IsSupported(e.Name)          // never extract anything that isn't audio
            && e.Length <= MaxAudioEntryBytes);
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80];
        }
        return cleaned.Length == 0 ? "sound" : cleaned;
    }

    private static string UniquePath(string folder, string name, string extension)
    {
        string candidate = Path.Combine(folder, name + extension);
        for (int n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{name} ({n}){extension}");
        }
        return candidate;
    }

    private static SoundBoard Clone(SoundBoard board) =>
        JsonSerializer.Deserialize<SoundBoard>(JsonSerializer.Serialize(board, JsonFile.Options), JsonFile.Options)!;

    private static void WriteJson<T>(ZipArchive zip, string entryName, T value)
    {
        using Stream stream = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(stream, value, JsonFile.Options);
    }

    private static T ReadJson<T>(ZipArchive zip, string entryName) where T : class
    {
        ZipArchiveEntry entry = zip.GetEntry(entryName)
                                ?? throw new InvalidDataException($"This file isn't a Jrik's Soundboard backup ({entryName} is missing).");
        using Stream stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonFile.Options)
               ?? throw new InvalidDataException($"{entryName} in the backup is empty or invalid.");
    }
}
