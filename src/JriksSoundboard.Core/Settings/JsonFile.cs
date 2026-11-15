using System.Text.Json;
using System.Text.Json.Serialization;

namespace JriksSoundboard.Core.Settings;

/// <summary>Shared JSON read/write with atomic saves (temp file + replace).</summary>
public static class JsonFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Read<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"File is empty or invalid: {path}");

    public static void WriteAtomic<T>(string path, T value)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, fullPath, overwrite: true);
    }
}

/// <summary>Where the app keeps its files. Overridable (the UI snapshot mode uses a temp folder).</summary>
public static class AppPaths
{
    public static string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JriksSoundboard");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string BoardFile => Path.Combine(DataDirectory, "board.json");
}
