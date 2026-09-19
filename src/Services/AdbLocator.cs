using System.Diagnostics;
using Microsoft.Win32;

namespace MuMuAdBlocker;

public static class AdbLocator
{
    private static readonly string[] MuMuProcesses = { "MuMuPlayer", "MuMuNxDevice", "MuMuVMHeadless", "NemuPlayer", "NemuHeadless", "MuMuVMMHeadless", "MuMuAndroidDevice" };

    public static bool IsMuMuRunning() => MuMuProcesses.Any(name => ProcessPaths(name).Any());

    private static IEnumerable<string> ProcessPaths(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            string? path = null;
            using (p)
            {
                try { path = p.MainModule?.FileName; }
                catch (System.ComponentModel.Win32Exception) { path = ""; }
                catch (InvalidOperationException) { }
            }
            if (path is not null) yield return path;
        }
    }

    public static IReadOnlyList<string> GetInstallRoots(string? savedAdb = null)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path.Trim('"'));
                if (Directory.Exists(path)) roots.Add(path);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        void FromFile(string? file)
        {
            if (string.IsNullOrWhiteSpace(file)) return;
            var dir = Path.GetDirectoryName(file);
            for (var n = 0; n < 4 && !string.IsNullOrEmpty(dir); n++, dir = Path.GetDirectoryName(dir)) Add(dir);
        }
        FromFile(savedAdb);
        foreach (var name in MuMuProcesses) foreach (var path in ProcessPaths(name)) FromFile(path);
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    try
                    {
                        using var key = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstall = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                        foreach (var name in uninstall?.GetSubKeyNames() ?? Array.Empty<string>())
                        {
                            using var item = uninstall!.OpenSubKey(name);
                            var display = item?.GetValue("DisplayName") as string ?? "";
                            if (!display.Contains("MuMu", StringComparison.OrdinalIgnoreCase)) continue;
                            Add(item?.GetValue("InstallLocation") as string);
                            var icon = item?.GetValue("DisplayIcon") as string;
                            if (icon is not null)
                            {
                                icon = icon.StartsWith('"') ? icon.Split('"').ElementAtOrDefault(1) : icon.Split(',')[0];
                                FromFile(icon);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
        }
        foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"C:\", @"D:\" })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            foreach (var parent in new[] { baseDir, Path.Combine(baseDir, "Netease"), Path.Combine(baseDir, "Program Files", "Netease") })
                foreach (var dir in EndpointDiscovery.SafeDirectories(parent, "MuMu*")) Add(dir);
            Add(Path.Combine(baseDir, "Nemu"));
        }
        return roots.ToArray();
    }

    public static async Task<string?> FindAsync(string? savedPath, CancellationToken ct = default)
    {
        var candidates = new List<string>();
        // Reuse the running ADB implementation first to avoid client/server version conflicts with MAA.
        foreach (var name in new[] { "adb", "adb_server" }) candidates.AddRange(ProcessPaths(name));
        if (!string.IsNullOrWhiteSpace(savedPath)) candidates.Add(savedPath);
        foreach (var root in GetInstallRoots(savedPath))
            foreach (var rel in new[] { "adb.exe", @"shell\adb.exe", @"nx_main\adb.exe", @"vmonitor\bin\adb_server.exe", @"emulator\nemu\vmonitor\bin\adb_server.exe" })
                candidates.Add(Path.Combine(root, rel));
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            foreach (var rel in new[] { "adb.exe", @"platform-tools\adb.exe", @"Android\Sdk\platform-tools\adb.exe" })
                candidates.Add(Path.Combine(root, rel));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(dir)) candidates.Add(Path.Combine(dir, "adb.exe"));
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (await IsValidAsync(path, ct)) return path;
        }
        return null;
    }

    public static async Task<bool> IsValidAsync(string adbPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath)) return false;
        try
        {
            var r = await AdbRunner.RunAsync(adbPath, new[] { "version" }, TimeSpan.FromSeconds(3), ct);
            return r.Success && r.StdOut.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}
