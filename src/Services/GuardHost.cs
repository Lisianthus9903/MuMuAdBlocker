namespace MuMuAdBlocker;

public static class GuardHost
{
    public static async Task<int> RunOnceAsync()
    {
        var store = new GuardStore();
        try
        {
            using var gate = store.TryLock();
            if (gate is null) return 0;
            if (store.IsDisabled) return 0;
            var state = store.Load();
            if (!state.Enabled) return 0;
            if (!AdbLocator.IsMuMuRunning())
            {
                store.Status(new(DateTimeOffset.UtcNow, "대기", 0, 0, 0, "MuMu 미실행. ADB/에뮬레이터를 시작하지 않음"));
                return 0;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var adbPath = await AdbLocator.FindAsync(state.AdbPath, deadline.Token);
            if (adbPath is null)
            {
                store.Status(new(DateTimeOffset.UtcNow, "확인 필요", 0, 0, 1, "ADB를 찾지 못했거나 실행 중인 ADB 경로에 접근할 수 없음. 공유 ADB를 교체하지 않았습니다."));
                return 2;
            }
            var adb = new AdbRunner(adbPath);
            state.AdbPath = adbPath; store.Save(state);
            var endpoints = await EndpointDiscovery.DiscoverAsync(adbPath, state.Endpoints, deadline.Token);
            await EndpointDiscovery.ConnectAsync(adb, endpoints, deadline.Token);
            var result = await adb.RunAsync(new[] { "devices" }, ct: deadline.Token);
            if (!result.Success) throw new IOException("ADB 장치 조회 실패: " + result.Combined);
            var manager = new MuMuManager(adb, store.Log);
            var status = await new ProtectionGuard(manager, store).ReconcileAsync(
                MuMuManager.ParseDeviceSerials(result.StdOut).ToArray(), state, deadline.Token);
            store.Status(status);
            return status.Failed > 0 ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            store.Status(new(DateTimeOffset.UtcNow, "시간 제한", 0, 0, 1, "45초 점검 제한. 다음 예약 실행에서 이어서 점검"));
            return 3;
        }
        catch (Exception ex)
        {
            try { store.Log(ex.Message); store.Status(new(DateTimeOffset.UtcNow, "오류", 0, 0, 1, ex.Message)); } catch (IOException) { }
            return 1;
        }
    }

    public static async Task<string> RestoreSavedAsync()
    {
        var store = new GuardStore();
        using var gate = store.TryLock() ?? throw new InvalidOperationException("다른 자동 점검이 진행 중입니다.");
        var state = store.Load();
        if (state.Enabled) throw new InvalidOperationException("자동 유지를 먼저 해제하십시오.");
        if (state.Backups.Count == 0) return "저장된 원래 권한 백업이 없습니다. 변경하지 않았습니다.";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var path = await AdbLocator.FindAsync(state.AdbPath, deadline.Token) ?? throw new IOException("ADB를 찾지 못했습니다.");
        var adb = new AdbRunner(path);
        await EndpointDiscovery.ConnectAsync(adb,
            await EndpointDiscovery.DiscoverAsync(path, state.Endpoints.Concat(state.Backups.Values.Select(b => b.Serial)), deadline.Token), deadline.Token);
        var restored = await new ProtectionGuard(new MuMuManager(adb, store.Log), store).RestoreAsync(state, deadline.Token);
        return $"원래 권한 복원 {restored}개. 미연결/식별 불일치 등으로 남은 백업 {state.Backups.Count}개.\n미연결 인스턴스는 실행 후 다시 복원하십시오. 백업은 자동 삭제하지 않습니다.";
    }
}
