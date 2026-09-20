using System.Runtime.Versioning;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml.Linq;
using System.Diagnostics;
using System.Text.Json;

namespace MuMuAdBlocker;

/// <summary>Current-user Task Scheduler registration. No service, password, elevation or startup script.</summary>
public static class GuardTask
{
    [SupportedOSPlatform("windows")]
    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows 사용자 식별 실패");
    [SupportedOSPlatform("windows")]
    public static string TaskName => "MuMuAdBlocker-Guard-" + UserSid;
    public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MuMuAdBlocker.lnk");
    [SupportedOSPlatform("windows")]
    private static dynamic Connect()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("작업 스케줄러를 사용할 수 없습니다.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }
    [SupportedOSPlatform("windows")]
    private static void Release(object? obj) { if (obj is not null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }

    [SupportedOSPlatform("windows")]
    public static string? ReadXml(string name)
    {
        object? service = null, root = null, task = null;
        try
        {
            service = Connect(); root = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)root).GetTask(name); return (string)((dynamic)task).Xml;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x8004130F)) { return null; }
        finally { Release(task); Release(root); Release(service); }
    }

    [SupportedOSPlatform("windows")]
    public static void Register(string name, string xml)
    {
        object? service = null, root = null, task = null;
        try
        {
            service = Connect(); root = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)root).RegisterTask(name, xml, 6, UserSid, null, 3, null);
        }
        finally { Release(task); Release(root); Release(service); }
    }

    [SupportedOSPlatform("windows")]
    public static void Delete(string name)
    {
        object? service = null, root = null;
        try
        {
            service = Connect(); root = ((dynamic)service).GetFolder("\\"); ((dynamic)root).DeleteTask(name, 0);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x8004130F)) { }
        finally { Release(root); Release(service); }
    }

    public static string BuildXml(string executable, string sid, DateTime start, string arguments = "--guard-once")
    {
        static string E(string s) => SecurityElement.Escape(s)!;
        return $"""
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>MuMuAdBlocker: local MuMu Store overlay permission check. Current user, once per minute, no resident GUI.</Description></RegistrationInfo>
          <Triggers>
            <TimeTrigger><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>{start.ToString("s", CultureInfo.InvariantCulture)}</StartBoundary><Enabled>true</Enabled></TimeTrigger>
            <LogonTrigger><Enabled>true</Enabled><UserId>{E(sid)}</UserId><Delay>PT10S</Delay></LogonTrigger>
          </Triggers>
          <Principals><Principal id="User"><UserId>{E(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT50S</ExecutionTimeLimit><Priority>7</Priority>
          </Settings>
          <Actions Context="User"><Exec><Command>{E(executable)}</Command><Arguments>{E(arguments)}</Arguments><WorkingDirectory>{E(Path.GetDirectoryName(executable)!)}</WorkingDirectory></Exec></Actions>
        </Task>
        """;
    }

    [SupportedOSPlatform("windows")]
    public static string Install(AppSettings settings)
    {
        var store = new GuardStore();
        using var gate = store.TryLock() ?? throw new InvalidOperationException("자동 점검이 진행 중입니다. 점검 종료 후 다시 시도하십시오.");
        var original = store.Load();
        var oldXml = ReadXml(TaskName);
        var oldShortcut = File.Exists(ShortcutPath) ? File.ReadAllBytes(ShortcutPath) : null;
        if (Directory.Exists(ShortcutPath)) throw new IOException("관리 바로가기 위치에 폴더가 있어 설치할 수 없습니다.");
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로 확인 실패");
        if (!Path.GetFileName(source).Equals("MuMuAdBlocker.exe", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(AppContext.BaseDirectory, "MuMuAdBlocker.runtimeconfig.json")))
            throw new InvalidOperationException("자동 유지 설치에는 배포용 단일 MuMuAdBlocker.exe를 사용하십시오.");
        string hash;
        using (var file = File.OpenRead(source)) hash = Convert.ToHexString(SHA256.HashData(file));
        var destination = Path.Combine(store.Root, "guard-bin", hash[..16], "MuMuAdBlocker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            var temporary = destination + ".tmp";
            File.Copy(source, temporary, true); File.Move(temporary, destination, true);
        }
        using (var file = File.OpenRead(destination))
            if (Convert.ToHexString(SHA256.HashData(file)) != hash) throw new IOException("자동 유지 실행 파일 검증 실패");
        // Actually execute the installed single EXE via the same Windows scheduler/principal.
        // This probe never touches Android or requires the guard lock held by this installer.
        VerifyScheduledExecutable(destination);
        original.AdbPath = settings.AdbPath;
        var endpoint = EndpointDiscovery.Normalize(settings.LastEndpoint);
        if (endpoint is not null && !original.Endpoints.Contains(endpoint)) original.Endpoints.Add(endpoint);
        original.Enabled = true;
        CommitConfiguration(store, original, () =>
        {
            Register(TaskName, BuildXml(destination, UserSid, DateTime.Now.AddMinutes(1)));
            var registered = ReadXml(TaskName) ?? throw new IOException("예약 작업 등록 확인 실패");
            if (!ValidateRegistrationXml(registered, destination))
                throw new IOException("예약 작업 실행 구성 검증 실패");
            WriteShortcut(destination);
        },
        () => { if (oldXml is not null) Register(TaskName, oldXml); else Delete(TaskName); },
        () => { if (oldShortcut is null) File.Delete(ShortcutPath); else File.WriteAllBytes(ShortcutPath, oldShortcut); });
        store.Log("자동 유지 설치 완료: " + destination);
        return destination;
    }

    public static bool ValidateRegistrationXml(string text, string executable, string arguments = "--guard-once")
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var xml = XDocument.Parse(text);
        // Task Scheduler omits values equal to schema defaults in its normalized XML.
        return xml.Descendants(ns + "Command").SingleOrDefault()?.Value == executable &&
            xml.Descendants(ns + "Arguments").SingleOrDefault()?.Value == arguments &&
            xml.Descendants(ns + "WorkingDirectory").SingleOrDefault()?.Value == Path.GetDirectoryName(executable) &&
            (xml.Descendants(ns + "RunLevel").SingleOrDefault()?.Value ?? "LeastPrivilege") == "LeastPrivilege" &&
            (xml.Descendants(ns + "Settings").SingleOrDefault()?.Element(ns + "Enabled")?.Value ?? "true") == "true";
    }

    internal static void CommitConfiguration(GuardStore store, GuardState state, Action activate, params Action[] rollback)
    {
        var before = File.Exists(store.StatePath) ? File.ReadAllText(store.StatePath) : null;
        var wasDisabled = store.IsDisabled;
        try
        {
            store.Save(state);
            activate();
            File.Delete(store.DisabledPath);
        }
        catch (Exception installError)
        {
            var errors = new List<Exception> { installError };
            // Attempt each rollback independently, even if one part cannot be restored.
            void Restore(Action action) { try { action(); } catch (Exception e) { errors.Add(e); } }
            Restore(() => { if (before is null) File.Delete(store.StatePath); else AtomicFile.Write(store.StatePath, before); });
            foreach (var action in rollback) Restore(action);
            Restore(() => { if (wasDisabled) AtomicFile.Write(store.DisabledPath, "disabled"); else File.Delete(store.DisabledPath); });
            if (errors.Count > 1)
            {
                AtomicFile.Write(store.DisabledPath, "installation rollback incomplete");
                // Older installed versions do not know the stop marker. Disable the JSON flag too.
                var disabled = store.Load(); disabled.Enabled = false; store.Save(disabled);
                throw new AggregateException("설치 복구 일부 실패. 자동 변경을 중지했습니다. 설정/백업은 보존하십시오.", errors);
            }
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyScheduledExecutable(string executable)
    {
        var name = "MuMuAdBlocker-Probe-" + Guid.NewGuid().ToString("N");
        object? service = null, root = null, task = null, running = null;
        try
        {
            Register(name, BuildXml(executable, UserSid, DateTime.Now.AddYears(1), "--smoke-test"));
            service = Connect(); root = ((dynamic)service).GetFolder("\\"); task = ((dynamic)root).GetTask(name);
            var before = (DateTime)((dynamic)task).LastRunTime;
            running = ((dynamic)task).Run(null);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                Thread.Sleep(100);
                var result = (int)((dynamic)task).LastTaskResult;
                if ((DateTime)((dynamic)task).LastRunTime > before && (int)((dynamic)task).State == 3 &&
                    result != 0x41301 && result != 0x41303)
                {
                    if (result != 0) throw new IOException($"설치 사본 예약 실행 실패: 0x{result:X8}");
                    return;
                }
            }
            throw new TimeoutException("설치 사본 예약 실행 확인 시간 초과");
        }
        finally
        {
            // Only our disposable probe instance may be stopped, never the shared guard/ADB/game.
            if (running is not null && task is not null)
                try { if ((int)((dynamic)task).State == 4) ((dynamic)running).Stop(); } catch (COMException) { }
            Release(running); Release(task); Release(root); Release(service);
            Delete(name);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteShortcut(string destination)
    {
        object? shell = null, link = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            link = ((dynamic)shell).CreateShortcut(ShortcutPath);
            ((dynamic)link).TargetPath = destination;
            ((dynamic)link).Arguments = "";
            ((dynamic)link).WorkingDirectory = Path.GetDirectoryName(destination)!;
            ((dynamic)link).Description = "MuMuAdBlocker 자동 유지 관리 · 해제 · 원래 권한 복원";
            ((dynamic)link).Save();
            Release(link); link = ((dynamic)shell).CreateShortcut(ShortcutPath);
            if ((string)((dynamic)link).TargetPath != destination || !File.Exists(ShortcutPath))
                throw new IOException("관리 바로가기 확인 실패");
        }
        finally { Release(link); Release(shell); }
    }

    [SupportedOSPlatform("windows")]
    public static string InstallationSummary()
    {
        var store = new GuardStore();
        if (store.IsDisabled || !store.Load().Enabled) return "자동 유지 꺼짐";
        var xml = ReadXml(TaskName);
        if (xml is null) return "예약 작업 없음 / 재설치 필요";
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = XDocument.Parse(xml);
        var command = task.Descendants(ns + "Command").SingleOrDefault()?.Value;
        if (command is null || !File.Exists(command)) return "설치 사본 없음 / 재설치 필요";
        var full = Path.GetFullPath(command);
        var prefix = Path.GetFullPath(Path.Combine(store.Root, "guard-bin")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !ValidateRegistrationXml(xml, full))
            return "자동 실행 구성 오류 / 재설치 필요";
        using var file = File.OpenRead(full);
        var hash = Convert.ToHexString(SHA256.HashData(file));
        if (Path.GetFileName(Path.GetDirectoryName(full)) != hash[..16]) return "설치 사본 손상 / 재설치 필요";
        return "자동 유지 설치됨 (광고 화면 효과는 별도 확인)";
    }

    [SupportedOSPlatform("windows")]
    public static void Disable()
    {
        var store = new GuardStore();
        using var gate = store.TryLock() ?? throw new InvalidOperationException("자동 점검이 진행 중입니다. 점검 종료 후 다시 시도하십시오.");
        AtomicFile.Write(store.DisabledPath, "disabled");
        try { var state = store.Load(); state.Enabled = false; store.Save(state); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { store.Log("설정/백업 손상: 원본을 보존하고 중지 표시 및 예약 작업 해제로 자동 유지를 중단합니다."); }
        Delete(TaskName); // If deletion fails, Enabled=false still prevents further mutations.
        store.Status(new(DateTimeOffset.UtcNow, "자동 유지 해제", 0, 0, 0, "현재 권한은 유지. 원래 권한 복원은 별도 기능 사용"));
    }
}
