using System.Runtime.Versioning;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml.Linq;

namespace MuMuAdBlocker;

/// <summary>Current-user Task Scheduler registration. No service, password, elevation or startup script.</summary>
public static class GuardTask
{
    [SupportedOSPlatform("windows")]
    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows 사용자 식별 실패");
    [SupportedOSPlatform("windows")]
    public static string TaskName => "MuMuAdBlocker-Guard-" + UserSid;
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
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x8004130F)) { return null; }
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
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x8004130F)) { }
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
        var before = System.Text.Json.JsonSerializer.Serialize(original);
        var oldXml = ReadXml(TaskName);
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
        original.AdbPath = settings.AdbPath;
        var endpoint = EndpointDiscovery.Normalize(settings.LastEndpoint);
        if (endpoint is not null && !original.Endpoints.Contains(endpoint)) original.Endpoints.Add(endpoint);
        original.Enabled = true;
        try
        {
            store.Save(original);
            Register(TaskName, BuildXml(destination, UserSid, DateTime.Now.AddMinutes(1)));
            var registered = ReadXml(TaskName) ?? throw new IOException("예약 작업 등록 확인 실패");
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var xml = XDocument.Parse(registered);
            if (xml.Descendants(ns + "Command").Single().Value != destination ||
                xml.Descendants(ns + "Arguments").Single().Value != "--guard-once") throw new IOException("예약 작업 실행 경로 검증 실패");
            store.Log("자동 유지 설치 완료: " + destination);
            return destination;
        }
        catch (Exception installError)
        {
            AtomicFile.Write(store.StatePath, before);
            try { if (oldXml is not null) Register(TaskName, oldXml); else Delete(TaskName); }
            catch (Exception rollbackError) { throw new AggregateException("예약 작업 설치 및 복구에 실패했습니다. 자동 유지 상태를 확인하십시오.", installError, rollbackError); }
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Disable()
    {
        var store = new GuardStore();
        using var gate = store.TryLock() ?? throw new InvalidOperationException("자동 점검이 진행 중입니다. 점검 종료 후 다시 시도하십시오.");
        var state = store.Load(); state.Enabled = false; store.Save(state);
        Delete(TaskName); // If deletion fails, Enabled=false still prevents further mutations.
        store.Status(new(DateTimeOffset.UtcNow, "자동 유지 해제", 0, 0, 0, "현재 권한은 유지. 원래 권한 복원은 별도 기능 사용"));
    }
}
