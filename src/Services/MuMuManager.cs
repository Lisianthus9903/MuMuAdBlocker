using System.Text.RegularExpressions;

namespace MuMuAdBlocker;

public enum AppOpState { Unknown, Default, Allow, Ignore, Deny }
public sealed record MuMuDevice(string Serial, bool StoreFound, string VersionName, string VersionCode, AppOpState OpState, string RawOpOutput)
{
    public bool IsMuMu => StoreFound;
}

/// <summary>Only local emulators with the known Store package are eligible. No version pinning.</summary>
public sealed class MuMuManager
{
    public const string StorePackage = "com.mumu.store";
    public const string AppOpName = "SYSTEM_ALERT_WINDOW";
    private readonly IAdbRunner _adb;
    private readonly Action<string> _log;
    public MuMuManager(IAdbRunner adb, Action<string> log) { _adb = adb; _log = log; }

    public async Task<List<MuMuDevice>> FindMuMuDevicesAsync(CancellationToken ct = default)
    {
        var r = await _adb.RunAsync(new[] { "devices" }, ct: ct);
        if (!r.Success) throw new InvalidOperationException("adb devices 실패: " + r.Combined.Trim());
        var result = new List<MuMuDevice>();
        foreach (var serial in ParseDeviceSerials(r.StdOut))
        {
            ct.ThrowIfCancellationRequested();
            try { var d = await InspectAsync(serial, ct); if (d is not null) result.Add(d); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log($"{serial}: 검사 실패: {ex.Message}"); }
        }
        return result;
    }

    public static IEnumerable<string> ParseDeviceSerials(string output) => output.Split('\n')
        .Select(l => Regex.Split(l.Trim(), @"\s+"))
        .Where(p => p.Length >= 2 && p[1] == "device" && EndpointDiscovery.IsLocalSerial(p[0]))
        .Select(p => p[0]).Distinct(StringComparer.Ordinal);

    public async Task<MuMuDevice?> InspectAsync(string serial, CancellationToken ct = default)
    {
        if (!EndpointDiscovery.IsLocalSerial(serial)) return null;
        var path = await Shell(serial, $"pm path --user 0 {StorePackage}", ct);
        if (!path.Success || !Regex.IsMatch(path.StdOut, @"(?m)^package:/")) return null;
        var props = await Shell(serial, "getprop", ct);
        if (!props.Success || !IsEmulator(props.StdOut)) return null;
        var (name, code) = await GetStoreVersionAsync(serial, ct);
        var (state, raw) = await GetAppOpStateAsync(serial, ct);
        return new(serial, true, name, code, state, raw);
    }

    public static bool IsEmulator(string props) => Regex.IsMatch(props,
        @"\[(?:ro\.kernel\.qemu|ro\.boot\.qemu)\]:\s*\[1\]|\[(?:ro\.product\.[^\]]+|ro\.hardware)\]:\s*\[[^\]]*(?:mumu|nemu|netease|ranchu|goldfish)",
        RegexOptions.IgnoreCase);

    public async Task<(string versionName, string versionCode)> GetStoreVersionAsync(string serial, CancellationToken ct = default)
    {
        var r = await Shell(serial, $"dumpsys package {StorePackage}", ct);
        if (!r.Success) return ("?", "?");
        var name = Regex.Match(r.StdOut, @"versionName=(\S+)");
        var code = Regex.Match(r.StdOut, @"versionCode=(\d+)");
        return (name.Success ? name.Groups[1].Value : "?", code.Success ? code.Groups[1].Value : "?");
    }

    public Task<ProcessResult> Shell(string serial, string command, CancellationToken ct = default)
    {
        if (!EndpointDiscovery.IsLocalSerial(serial)) throw new ArgumentException("로컬 에뮬레이터만 지원합니다.");
        return _adb.RunAsync(new[] { "-s", serial, "shell", command }, TimeSpan.FromSeconds(5), ct);
    }

    public static bool HasCommandError(string text) => Regex.IsMatch(text,
        @"(?im)^\s*(?:error\b|exception\b|securityexception\b|java\.[\w.]*exception\b|permission denial\b|unknown (?:operation|command|package)\b|no uid for\b)");

    public static AppOpState ParseAppOp(ProcessResult r)
    {
        if (!r.Success || HasCommandError(r.Combined)) return AppOpState.Unknown;
        // A UID-wide override can take precedence over the package mode. Do not report a false block.
        if (Regex.IsMatch(r.StdOut, @"(?im)^\s*Uid mode:")) return AppOpState.Unknown;
        var m = Regex.Match(r.StdOut, @"(?im)^\s*SYSTEM_ALERT_WINDOW\s*[:=]\s*(\w+)");
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant() switch
        {
            "ignore" => AppOpState.Ignore, "deny" => AppOpState.Deny,
            "allow" => AppOpState.Allow, "default" => AppOpState.Default, _ => AppOpState.Unknown
        };
        // Only explicit empty-operations output means default. An error/empty output is not success.
        return Regex.IsMatch(r.StdOut, @"(?im)^\s*No operations\.?\s*$")
            ? AppOpState.Default : AppOpState.Unknown;
    }

    public async Task<(AppOpState state, string raw)> GetAppOpStateAsync(string serial, CancellationToken ct = default)
    {
        var r = await Shell(serial, $"cmd appops get --user 0 {StorePackage} {AppOpName}", ct);
        return (ParseAppOp(r), r.Combined.Trim());
    }

    public async Task<(bool ok, AppOpState finalState, string message)> SetAppOpAsync(string serial, string mode, CancellationToken ct = default)
    {
        var expected = mode switch
        {
            "ignore" => AppOpState.Ignore, "default" => AppOpState.Default,
            "allow" => AppOpState.Allow, "deny" => AppOpState.Deny,
            _ => throw new ArgumentException("지원하지 않는 AppOps 모드입니다.", nameof(mode))
        };
        if (await InspectAsync(serial, ct) is null) return (false, AppOpState.Unknown, "지원 대상 MuMu를 확인하지 못해 변경하지 않았습니다.");
        _log($"{serial}: {AppOpName} → {mode}");
        var set = await Shell(serial, $"cmd appops set --user 0 {StorePackage} {AppOpName} {mode}", ct);
        if (!set.Success || HasCommandError(set.Combined)) return (false, AppOpState.Unknown, "AppOps 설정 실패: " + set.Combined.Trim());
        var (state, raw) = await GetAppOpStateAsync(serial, ct);
        if (state != expected) return (false, state, "변경 후 상태 검증 실패: " + raw);
        return (true, state, "권한 검증 완료: " + Describe(state));
    }

    public static string Describe(AppOpState s) => s switch
    {
        AppOpState.Default => "기본 상태", AppOpState.Allow => "오버레이 허용",
        AppOpState.Ignore => "Store 오버레이 권한 차단", AppOpState.Deny => "Store 오버레이 권한 거부",
        _ => "미확인 / 지원 여부 점검 필요"
    };
}
