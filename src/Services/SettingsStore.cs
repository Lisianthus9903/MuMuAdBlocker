using System.Text.Json;

namespace MuMuAdBlocker;

public sealed class AppSettings
{
    public string AdbPath { get; set; } = string.Empty;
    public string LastDevice { get; set; } = string.Empty;
    public string LastEndpoint { get; set; } = string.Empty;
}

public static class SettingsStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuMuAdBlocker");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static string LogDirectory => Path.Combine(DirectoryPath, "logs");

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    public static void Save(AppSettings settings) => AtomicFile.Write(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
}

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(content); writer.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
