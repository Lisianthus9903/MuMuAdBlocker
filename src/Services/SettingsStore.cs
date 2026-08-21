using System.Text.Json;

namespace MuMuAdBlocker;

/// <summary>%LOCALAPPDATA%\MuMuAdBlocker\settings.json 에 저장되는 사용자 설정</summary>
public sealed class AppSettings
{
    public string AdbPath { get; set; } = string.Empty;
    public string LastDevice { get; set; } = string.Empty;
    public string LastEndpoint { get; set; } = string.Empty;
}

public static class SettingsStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuMuAdBlocker");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s is not null) return s;
            }
        }
        catch { /* 손상된 설정은 무시하고 기본값 사용 */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }

    public static string LogDirectory => Path.Combine(Dir, "logs");
}
