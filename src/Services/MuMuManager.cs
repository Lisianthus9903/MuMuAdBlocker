using System.Text.RegularExpressions;

namespace MuMuAdBlocker;

public enum AppOpState { Unknown, Default, Allow, Ignore, Deny }

public sealed record MuMuDevice(string Serial, bool StoreFound, string VersionName, string VersionCode, AppOpState OpState, string RawOpOutput)
{
    public bool IsMuMu => StoreFound;
}

/// <summary>MuMu 장치 탐색 및 AppOps 관리 로직</summary>
public sealed class MuMuManager
{
    public const string StorePackage = "com.mumu.store";
    public const string AppOpName = "SYSTEM_ALERT_WINDOW";

    private readonly AdbRunner _adb;
    private readonly Action<string> _log;

    public MuMuManager(AdbRunner adb, Action<string> log)
    {
        _adb = adb;
        _log = log;
    }

    /// <summary>연결된 장치 중 com.mumu.store 를 가진 MuMu 인스턴스만 반환</summary>
    public async Task<List<MuMuDevice>> FindMuMuDevicesAsync(CancellationToken ct = default)
    {
        var result = new List<MuMuDevice>();
        var r = await _adb.RunAsync(new[] { "devices" }, ct: ct);
        if (!r.Success) throw new InvalidOperationException("adb devices 실행 실패: " + r.Combined.Trim());

        var serials = new List<(string serial, string state)>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Trim().Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) serials.Add((parts[0], parts[1]));
        }

        foreach (var (serial, state) in serials)
        {
            if (state != "device")
            {
                _log($"장치 {serial}: 상태 {state} (사용 불가)");
                continue;
            }
            var dev = await InspectAsync(serial, ct);
            if (dev is not null) result.Add(dev);
        }
        return result;
    }

    /// <summary>장치가 MuMu인지 검사하고 상세 정보를 반환. 아니면 null.</summary>
    public async Task<MuMuDevice?> InspectAsync(string serial, CancellationToken ct = default)
    {
        var path = await _adb.RunAsync(new[] { "-s", serial, "shell", $"pm path {StorePackage}" }, ct: ct);
        if (!path.Success || !path.StdOut.Contains("package:", StringComparison.Ordinal))
            return null;

        var (verName, verCode) = await GetStoreVersionAsync(serial, ct);
        var (opState, raw) = await GetAppOpStateAsync(serial, ct);
        return new MuMuDevice(serial, true, verName, verCode, opState, raw);
    }

    public async Task<(string versionName, string versionCode)> GetStoreVersionAsync(string serial, CancellationToken ct = default)
    {
        var r = await _adb.RunAsync(new[] { "-s", serial, "shell", $"dumpsys package {StorePackage}" }, ct: ct);
        if (!r.Success) return ("?", "?");
        var name = Regex.Match(r.StdOut, @"versionName=(\S+)");
        var code = Regex.Match(r.StdOut, @"versionCode=(\d+)");
        return (name.Success ? name.Groups[1].Value : "?", code.Success ? code.Groups[1].Value : "?");
    }

    /// <summary>AppOps 현재 상태 조회 (출력 형식 차이에 robust하게 파싱)</summary>
    public async Task<(AppOpState state, string raw)> GetAppOpStateAsync(string serial, CancellationToken ct = default)
    {
        var r = await _adb.RunAsync(new[] { "-s", serial, "shell",
            $"cmd appops get --user 0 {StorePackage} {AppOpName}" }, ct: ct);
        var raw = r.Combined.Trim();
        if (!r.Success && string.IsNullOrWhiteSpace(raw))
            return (AppOpState.Unknown, raw);

        // 예: "SYSTEM_ALERT_WINDOW: ignore" 또는 "SYSTEM_ALERT_WINDOW: allow; time=... duration=..."
        var m = Regex.Match(raw, @"SYSTEM_ALERT_WINDOW\s*[:=]\s*(\w+)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            // 모드가 출력되지 않으면 default 로 간주 (일부 버전은 모드가 default일 때 값 생략)
            return (AppOpState.Default, raw);
        }
        return m.Groups[1].Value.ToLowerInvariant() switch
        {
            "ignore" => (AppOpState.Ignore, raw),
            "deny" => (AppOpState.Deny, raw),
            "allow" => (AppOpState.Allow, raw),
            "default" => (AppOpState.Default, raw),
            _ => (AppOpState.Unknown, raw),
        };
    }

    /// <summary>AppOps 모드 설정 후 실제 상태를 재조회하여 검증</summary>
    public async Task<(bool ok, AppOpState finalState, string message)> SetAppOpAsync(string serial, string mode, CancellationToken ct = default)
    {
        _log($"AppOps 설정: {AppOpName} → {mode} ({serial})");
        var set = await _adb.RunAsync(new[] { "-s", serial, "shell",
            $"cmd appops set --user 0 {StorePackage} {AppOpName} {mode}" }, ct: ct);
        if (!set.Success)
            return (false, AppOpState.Unknown, "AppOps 명령이 실패했습니다.\n" + set.Combined.Trim());

        var (state, raw) = await GetAppOpStateAsync(serial, ct);
        if (state == AppOpState.Unknown)
            return (false, state, "설정 후 상태를 확인할 수 없습니다.\n" + raw);

        if (string.Equals(mode, "ignore", StringComparison.OrdinalIgnoreCase) && state != AppOpState.Ignore)
            return (false, state, $"설정이 적용되지 않았습니다. 현재 상태: {Describe(state)}\n" + raw);
        if (string.Equals(mode, "default", StringComparison.OrdinalIgnoreCase) && state != AppOpState.Default)
            return (false, state, $"복원이 적용되지 않았습니다. 현재 상태: {Describe(state)}\n" + raw);

        return (true, state, $"검증 완료: {Describe(state)}");
    }

    public static string Describe(AppOpState s) => s switch
    {
        AppOpState.Default => "기본 상태",
        AppOpState.Allow => "허용됨",
        AppOpState.Ignore => "광고 오버레이 차단됨",
        AppOpState.Deny => "차단됨",
        _ => "알 수 없음",
    };
}
