using System.Diagnostics;
using System.IO;

namespace MuMuAdBlocker;

/// <summary>adb.exe 자동 탐색. 시스템 전체 재귀 검색은 하지 않는다.</summary>
public static class AdbLocator
{
    /// <summary>우선순위 순서로 adb 후보를 탐색한다.</summary>
    public static async Task<string?> FindAsync(string? savedPath, CancellationToken ct = default)
    {
        // 1. 이전에 저장한 경로
        if (!string.IsNullOrWhiteSpace(savedPath) && await IsValidAsync(savedPath)) return savedPath;

        // 2. 실행 프로그램과 같은 디렉터리
        var exeDir = AppContext.BaseDirectory;
        var local = Path.Combine(exeDir, "adb.exe");
        if (await IsValidAsync(local)) return local;
        var localSub = Path.Combine(exeDir, "platform-tools", "adb.exe");
        if (await IsValidAsync(localSub)) return localSub;

        // 3. 시스템 PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Path.IsPathRooted(dir)) continue;
                var candidate = Path.Combine(dir, "adb.exe");
                if (File.Exists(candidate) && await IsValidAsync(candidate)) return candidate;
            }
            catch { }
        }

        // 4~7. 일반적인 설치 위치
        var roots = new List<string>();
        void AddRoot(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(p);
        }
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddRoot("C:\\Program Files");
        AddRoot("D:\\");
        AddRoot("C:\\");

        var relativeCandidates = new[]
        {
            "Android\\platform-tools\\adb.exe",
            "Android\\android-sdk\\platform-tools\\adb.exe",
            "AppData\\Local\\Android\\Sdk\\platform-tools\\adb.exe",
            "platform-tools\\adb.exe",
            // MAA
            "MAA\\adb\\platform-tools\\adb.exe",
            "MAA\\bin\\adb.exe",
            // MuMu
            "Netease\\MuMuPlayer-6.0\\shell\\adb.exe",
            "Netease\\MuMuPlayerGlobal-6.0\\shell\\adb.exe",
            "Netease\\MuMu Player 12\\shell\\adb.exe",
            "MuMu\\emulator\\nemu\\vmonitor\\bin\\adb_server.exe",
            "Program Files\\Netease\\MuMuPlayer-6.0\\shell\\adb.exe",
            "Program Files\\Netease\\MuMuPlayerGlobal-6.0\\shell\\adb.exe",
        };

        foreach (var root in roots)
        {
            foreach (var rel in relativeCandidates)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = Path.Combine(root, rel);
                if (File.Exists(candidate) && await IsValidAsync(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>파일 존재 + 실제 실행(adb version) 성공 여부</summary>
    public static async Task<bool> IsValidAsync(string adbPath)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath)) return false;
        try
        {
            var r = await AdbRunner.RunAsync(adbPath, new[] { "version" }, TimeSpan.FromSeconds(8));
            return r.Success && r.StdOut.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
