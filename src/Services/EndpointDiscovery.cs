using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuMuAdBlocker;

public static class EndpointDiscovery
{
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim(), @"^(127\.0\.0\.1|localhost|\[::1\]):([0-9]{1,5})$", RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var port) || port is < 1 or > 65535) return null;
        return (m.Groups[1].Value == "[::1]" ? "[::1]:" : "127.0.0.1:") + port;
    }

    public static bool IsLocalSerial(string? serial) => serial is not null &&
        (Normalize(serial) is not null || Regex.IsMatch(serial, @"^emulator-[0-9]{4,5}$"));

    public static IReadOnlyList<string> ParseConfig(string json)
    {
        var ports = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            void Walk(JsonElement element, string prefix)
            {
                if (element.ValueKind == JsonValueKind.Object)
                    foreach (var p in element.EnumerateObject())
                    {
                        var name = (prefix + "." + p.Name).Trim('.').ToLowerInvariant();
                        var key = p.Name.ToLowerInvariant();
                        if ((key is "adb_port" or "adbport" or "adb.host_port" or "adb.port" ||
                             name.EndsWith(".adb.host_port") || name == "adb.host_port" || name.EndsWith(".adb.port") || name == "adb.port") &&
                            int.TryParse(p.Value.ToString(), out var port) && port is > 0 and <= 65535) ports.Add(port);
                        Walk(p.Value, name);
                    }
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var e in element.EnumerateArray()) Walk(e, prefix);
            }
            Walk(doc.RootElement, "");
        }
        catch (JsonException) { }
        return ports.Order().Select(p => "127.0.0.1:" + p).ToArray();
    }

    /// <summary>MuMuManager's read-only info command is also used by MaaFramework's device finder.</summary>
    public static IReadOnlyList<string> ParseManagerInfo(string json)
    {
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            void Walk(JsonElement node)
            {
                if (node.ValueKind == JsonValueKind.Object)
                {
                    if (node.TryGetProperty("adb_port", out var port) && int.TryParse(port.ToString(), out var number))
                    {
                        var host = node.TryGetProperty("adb_host_ip", out var ip) ? ip.ToString() :
                            node.TryGetProperty("adb_host", out ip) ? ip.ToString() : "127.0.0.1";
                        var ep = Normalize((host == "::1" ? "[::1]" : host) + ":" + number);
                        if (ep is not null) endpoints.Add(ep);
                    }
                    foreach (var property in node.EnumerateObject()) Walk(property.Value);
                }
                else if (node.ValueKind == JsonValueKind.Array)
                    foreach (var child in node.EnumerateArray()) Walk(child);
            }
            Walk(doc.RootElement);
        }
        catch (JsonException) { }
        return endpoints.Order(StringComparer.Ordinal).ToArray();
    }

    public static async Task<IReadOnlyList<string>> DiscoverAsync(string adbPath, IEnumerable<string> saved, CancellationToken ct = default)
    {
        var roots = AdbLocator.GetInstallRoots(adbPath);
        var result = new HashSet<string>(Find(roots, saved), StringComparer.Ordinal);
        var managers = roots.SelectMany(root => new[] { Path.Combine(root, "MuMuManager.exe"),
            Path.Combine(root, "nx_main", "MuMuManager.exe"), Path.Combine(root, "shell", "MuMuManager.exe") })
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Take(8);
        foreach (var manager in managers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = await AdbRunner.RunAsync(manager, new[] { "info", "--vmindex", "all" }, TimeSpan.FromSeconds(3), ct);
                if (info.Success) foreach (var endpoint in ParseManagerInfo(info.StdOut)) result.Add(endpoint);
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> Find(IEnumerable<string> roots, IEnumerable<string> saved)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in saved) { var e = Normalize(s); if (e is not null) result.Add(e); }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Take(64))
        {
            // Read only small known VM configuration files; never scan the entire drive.
            foreach (var vm in SafeDirectories(Path.Combine(root, "vms")).Take(64))
                foreach (var file in new[] { Path.Combine(vm, "configs", "extra_config.json"),
                    Path.Combine(vm, "configs", "vm_config.json"), Path.Combine(vm, "vm_config.json") })
                    try
                    {
                        if (new FileInfo(file) is { Exists: true, Length: <= 1048576 })
                            foreach (var ep in ParseConfig(File.ReadAllText(file))) result.Add(ep);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
        }
        // Fallbacks are attempted only if a socket is already listening locally.
        result.Add("127.0.0.1:16384"); result.Add("127.0.0.1:7555");
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] SafeDirectories(string root, string pattern = "*")
    {
        try { return Directory.GetDirectories(root, pattern, SearchOption.TopDirectoryOnly); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static async Task ConnectAsync(IAdbRunner adb, IEnumerable<string> endpoints, CancellationToken ct)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (var ep in endpoints.Select(Normalize).OfType<string>().Distinct().Take(128))
        {
            ct.ThrowIfCancellationRequested();
            var port = int.Parse(ep[(ep.LastIndexOf(':') + 1)..]);
            if (!listeners.Any(l => l.Port == port && (IPAddress.IsLoopback(l.Address) ||
                l.Address.Equals(IPAddress.Any) || l.Address.Equals(IPAddress.IPv6Any)))) continue;
            try { await adb.RunAsync(new[] { "connect", ep }, TimeSpan.FromSeconds(2), ct); }
            catch (TimeoutException) { }
        }
    }
}
