using System.Diagnostics;
using System.Text.Json;

namespace MuMuAdBlocker;

/// <summary>Fresh host evidence, independent of the Android model selected by the user.</summary>
public static class MuMuHostInventory
{
    public static async Task<IReadOnlyList<string>> ReadAsync(CancellationToken ct = default)
    {
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        // Do not promote saved endpoints or generic port guesses to identity evidence.
        var managers = AdbLocator.GetInstallRoots().SelectMany(root => new[] {
            Path.Combine(root, "MuMuManager.exe"), Path.Combine(root, "nx_main", "MuMuManager.exe"),
            Path.Combine(root, "shell", "MuMuManager.exe") })
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Take(8);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        foreach (var manager in managers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = await AdbRunner.RunAsync(manager, new[] { "info", "--vmindex", "all" },
                    TimeSpan.FromSeconds(3), budget.Token);
                if (info.Success)
                    foreach (var ep in ParseRunning(info.StdOut, pid => IsRunningInstance(manager, pid))) endpoints.Add(ep);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (TimeoutException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return endpoints.Order(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<string> ParseRunning(string json, Func<int, bool> isRunningInstance)
    {
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            void ReadRow(JsonElement row)
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !ReadInt(row, "error_code", out var code) || code != 0 ||
                    !row.TryGetProperty("is_android_started", out var android) || android.ValueKind != JsonValueKind.True ||
                    !row.TryGetProperty("is_process_started", out var process) || process.ValueKind != JsonValueKind.True ||
                    !row.TryGetProperty("info_source", out var source) || source.ValueKind != JsonValueKind.String || source.GetString() != "rpc" ||
                    !ReadInt(row, "pid", out var id) || id <= 0 ||
                    !row.TryGetProperty("adb_host_ip", out var host) || host.ValueKind != JsonValueKind.String ||
                    !ReadInt(row, "adb_port", out var number)) return;
                var ip = host.GetString();
                var ep = EndpointDiscovery.Normalize((ip == "::1" ? "[::1]" : ip) + ":" + number);
                if (ep is not null && isRunningInstance(id)) endpoints.Add(ep);
            }
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) foreach (var row in root.EnumerateArray()) ReadRow(row);
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("index", out _)) ReadRow(root);
                else foreach (var row in root.EnumerateObject()) ReadRow(row.Value);
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { } // Wrong JSON types are not identity evidence.
        return endpoints.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool ReadInt(JsonElement row, string name, out int value)
    {
        value = 0;
        return row.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
    }

    internal static bool IsInstancePath(string managerPath, string processPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(managerPath))!;
        var root = Path.GetFileName(dir) is "nx_main" or "shell" ? Path.GetDirectoryName(dir)! : dir;
        var path = Path.GetFullPath(processPath);
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFileName(path).Equals("MuMuNxDevice.exe", StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(path).Equals("MuMuPlayer.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRunningInstance(string manager, int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return !process.HasExited && path is not null && IsInstancePath(manager, path);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
