using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuMuAdBlocker;

public sealed class GuardBackup
{
    public string Serial { get; set; } = "";
    public string AndroidId { get; set; } = "";
    public string StoreVersion { get; set; } = "";
    public AppOpState OriginalMode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class GuardState
{
    public int Schema { get; set; } = 1;
    public bool Enabled { get; set; }
    public string AdbPath { get; set; } = "";
    public List<string> Endpoints { get; set; } = new();
    public Dictionary<string, GuardBackup> Backups { get; set; } = new();
    public int Cursor { get; set; }
}

public sealed record GuardStatus(DateTimeOffset CheckedUtc, string State, int Verified, int Repaired, int Failed, string Message);

public sealed class GuardStore
{
    public string Root { get; }
    public string StatePath => Path.Combine(Root, "guard.json");
    public string StatusPath => Path.Combine(Root, "guard-status.json");
    public GuardStore(string? root = null) { Root = root ?? SettingsStore.DirectoryPath; }
    public FileStream? TryLock()
    {
        Directory.CreateDirectory(Root);
        try { return new FileStream(Path.Combine(Root, "guard.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
    public GuardState Load()
    {
        if (!File.Exists(StatePath)) return new();
        var state = JsonSerializer.Deserialize<GuardState>(File.ReadAllText(StatePath));
        if (state is null || state.Schema != 1 || state.Endpoints is null || state.Backups is null ||
            state.Backups.Any(b => b.Value is null || b.Value.OriginalMode == AppOpState.Unknown || !Enum.IsDefined(b.Value.OriginalMode)))
            throw new InvalidDataException("자동 유지 설정/백업이 손상되었거나 지원하지 않는 형식입니다. 변경하지 않았습니다.");
        return state;
    }
    public void Save(GuardState state) => AtomicFile.Write(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    public void Status(GuardStatus status) => AtomicFile.Write(StatusPath, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    public string StatusSummary()
    {
        if (!File.Exists(StatusPath)) return "아직 자동 점검 기록 없음";
        var s = JsonSerializer.Deserialize<GuardStatus>(File.ReadAllText(StatusPath));
        return s is null ? "점검 상태 미확인" : $"{s.CheckedUtc.ToLocalTime():MM-dd HH:mm:ss} · {s.State} · 확인 {s.Verified}, 복구 {s.Repaired}, 실패 {s.Failed} · {s.Message}";
    }
    public void Log(string message)
    {
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, "guard.log");
        if (File.Exists(file) && new FileInfo(file).Length >= 1024 * 1024) File.Move(file, file + ".1", true);
        File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
}

/// <summary>One bounded, idempotent reconciliation. Host scheduling is separate and non-resident.</summary>
public sealed class ProtectionGuard
{
    private readonly MuMuManager _manager;
    private readonly GuardStore _store;
    public ProtectionGuard(MuMuManager manager, GuardStore store) { _manager = manager; _store = store; }

    public async Task<GuardStatus> ReconcileAsync(IReadOnlyList<string> serials, GuardState state, CancellationToken ct = default)
    {
        var verified = 0; var repaired = 0; var failed = 0;
        var start = serials.Count == 0 ? 0 : (int)((uint)state.Cursor % serials.Count);
        for (var i = 0; i < serials.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var index = (start + i) % serials.Count;
            var serial = serials[index];
            // Advance before slow I/O so a repeatedly failing VM cannot starve later VMs next minute.
            state.Cursor = (index + 1) % serials.Count;
            _store.Save(state);
            try
            {
                var d = await _manager.InspectAsync(serial, ct);
                if (d is null) { failed++; _store.Log($"{serial}: 지원 대상 확인 불가; 변경 없음"); continue; }
                if (d.OpState is AppOpState.Ignore or AppOpState.Deny) { verified++; continue; }
                if (d.OpState == AppOpState.Unknown) { failed++; _store.Log($"{serial}: AppOps 미확인: {d.RawOpOutput}"); continue; }
                var id = await ReadIdentityAsync(serial, ct);
                if (id is null) { failed++; _store.Log($"{serial}: Android 식별자 확인 불가; 변경 없음"); continue; }
                var key = serial + "|" + id;
                if (!state.Backups.ContainsKey(key))
                {
                    state.Backups.Add(key, new GuardBackup { Serial = serial, AndroidId = id,
                        StoreVersion = d.VersionName, OriginalMode = d.OpState, CreatedUtc = DateTimeOffset.UtcNow });
                    _store.Save(state); // Durable original-mode snapshot MUST precede any mutation.
                }
                var (ok, _, message) = await _manager.SetAppOpAsync(serial, "ignore", ct);
                _store.Log($"{serial} Store {d.VersionName}: {message}");
                if (ok) { verified++; repaired++; } else failed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; _store.Log($"{serial}: {ex.Message}"); }
        }
        return new(DateTimeOffset.UtcNow, failed > 0 ? "확인 필요" : verified > 0 ? "권한 확인 완료" : "대기",
            verified, repaired, failed, serials.Count == 0 ? "연결된 로컬 MuMu 없음 / ADB 설정 확인" : "Store 오버레이 권한만 확인; 화면의 모든 광고 제거를 뜻하지 않음");
    }

    public async Task<int> RestoreAsync(GuardState state, CancellationToken ct = default)
    {
        if (state.Enabled) throw new InvalidOperationException("자동 유지를 먼저 해제하십시오.");
        var restored = 0;
        foreach (var (key, backup) in state.Backups.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (!EndpointDiscovery.IsLocalSerial(backup.Serial)) continue;
            try
            {
                if (await _manager.InspectAsync(backup.Serial, ct) is null || await ReadIdentityAsync(backup.Serial, ct) != backup.AndroidId) continue;
                var (ok, _, message) = await _manager.SetAppOpAsync(backup.Serial, backup.OriginalMode.ToString().ToLowerInvariant(), ct);
                _store.Log($"원래 권한 복원 {backup.Serial}: {message}");
                if (ok) { state.Backups.Remove(key); _store.Save(state); restored++; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _store.Log($"복원 대기 {backup.Serial}: {ex.Message}"); }
        }
        return restored;
    }

    private async Task<string?> ReadIdentityAsync(string serial, CancellationToken ct)
    {
        var r = await _manager.Shell(serial, "settings --user 0 get secure android_id", ct);
        var id = r.StdOut.Trim();
        return r.Success && Regex.IsMatch(id, @"\A[0-9a-fA-F]{8,32}\z") ? id.ToLowerInvariant() : null;
    }
}
