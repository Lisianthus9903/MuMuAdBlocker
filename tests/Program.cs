using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using MuMuAdBlocker;

internal static class Program
{
    private static int _passed, _failed, _skipped;
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.FirstOrDefault() == "--fixture-sleep") { await Task.Delay(15000); return 0; }
        if (args.FirstOrDefault() == "--fixture-echo") { Console.WriteLine(args[1]); Console.Error.WriteLine("진단"); return 0; }
        foreach (var (text, code, expected) in new (string, int, AppOpState)[] {
            ("SYSTEM_ALERT_WINDOW: ignore", 0, AppOpState.Ignore),
            ("  SYSTEM_ALERT_WINDOW: allow; time=+1s", 0, AppOpState.Allow),
            ("SYSTEM_ALERT_WINDOW=deny", 0, AppOpState.Deny),
            ("SYSTEM_ALERT_WINDOW: default", 0, AppOpState.Default),
            ("No operations.", 0, AppOpState.Default),
            ("No operations.\nDefault mode: default", 0, AppOpState.Default),
            ("", 0, AppOpState.Unknown), ("Error: No UID for com.mumu.store", 0, AppOpState.Unknown),
            ("Unknown operation SYSTEM_ALERT_WINDOW", 0, AppOpState.Unknown),
            ("java.lang.SecurityException: denied", 0, AppOpState.Unknown),
            ("SYSTEM_ALERT_WINDOW: ignore", 1, AppOpState.Unknown),
            ("SYSTEM_ALERT_WINDOW: foreground", 0, AppOpState.Unknown),
            ("Uid mode: ignore", 0, AppOpState.Unknown),
            ("Uid mode: SYSTEM_ALERT_WINDOW: allow\nSYSTEM_ALERT_WINDOW: ignore", 0, AppOpState.Unknown),
            ("unrecognized future format", 0, AppOpState.Unknown) })
            await Test("AppOps " + text, () => { Eq(expected, MuMuManager.ParseAppOp(new(code, text, ""))); return Task.CompletedTask; });

        await Test("stderr permission error never success", () => {
            Eq(AppOpState.Unknown, MuMuManager.ParseAppOp(new(0, "No operations.", "Permission denial: appops"))); return Task.CompletedTask; });
        foreach (var ep in new[] { "192.168.1.1:5555", "USB123", "127.0.0.1:0", "127.0.0.1:65536", "127.0.0.1:1;reboot", "localhost.evil:1234", "127.0.0.1:-1", "127.0.0.1:abc" })
            await Test("reject endpoint " + ep, () => { Eq<string?>(null, EndpointDiscovery.Normalize(ep)); return Task.CompletedTask; });
        await Test("local endpoints", () => { Eq("127.0.0.1:16384", EndpointDiscovery.Normalize(" localhost:16384 ")); Eq("[::1]:7555", EndpointDiscovery.Normalize("[::1]:7555")); return Task.CompletedTask; });
        await Test("devices whitespace, daemon lines, offline, USB and remote filtering", () => {
            var result = MuMuManager.ParseDeviceSerials("* daemon started successfully\nList of devices attached\n127.0.0.1:16384 device product:mumu\nUSB123\tdevice\n192.168.1.3:5555 device\nemulator-5554\tdevice\n127.0.0.1:7555 offline\n127.0.0.1:16384\tdevice");
            Eq("127.0.0.1:16384,emulator-5554", string.Join(',', result)); return Task.CompletedTask; });
        await Test("config format variants and invalid ports", () => {
            var r = EndpointDiscovery.ParseConfig("{\"vms\":[{\"adb_port\":16416},{\"adb\":{\"host_port\":\"16448\"}},{\"adb.host_port\":16480},{\"port\":9999},{\"adb_port\":99999}]}");
            Eq("127.0.0.1:16416,127.0.0.1:16448,127.0.0.1:16480", string.Join(',', r)); Eq(0, EndpointDiscovery.ParseConfig("{").Count); return Task.CompletedTask; });
        await Test("manager info new versions, arrays, legacy nested port, remote exclusion", () => {
            var info = "{\"0\":{\"index\":\"0\",\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384},\"1\":{\"adb_host_ip\":\"192.168.1.8\",\"adb_port\":16416},\"2\":{\"adb_host\":\"::1\",\"adb_port\":16448}}";
            Eq("127.0.0.1:16384,[::1]:16448", string.Join(',', EndpointDiscovery.ParseManagerInfo(info)));
            Eq("127.0.0.1:16480", EndpointDiscovery.ParseManagerInfo("[{\"adb_port\":16480}]").Single());
            Eq("127.0.0.1:16480", EndpointDiscovery.ParseConfig("{\"adb\":{\"port\":16480}}").Single());
            Eq(0, EndpointDiscovery.ParseManagerInfo("not JSON").Count); return Task.CompletedTask;
        });
        await Test("emulator identity", () => {
            Yes(MuMuManager.IsEmulator("[ro.product.model]: [MuMu]") && MuMuManager.IsEmulator("[ro.kernel.qemu]: [1]"));
            Yes(!MuMuManager.IsEmulator("[ro.product.model]: [Pixel 9]\n[ro.kernel.qemu]: [0]")); return Task.CompletedTask; });

        await Test("atomic state, corrupt state fails closed, exclusive lock", async () => {
            await Temp(async store => {
                var state = new GuardState { Enabled = true }; store.Save(state); Yes(store.Load().Enabled);
                using (var gate = store.TryLock()) { Yes(gate is not null); using var second = store.TryLock(); Yes(second is null); }
                using (var gate = store.TryLock()) Yes(gate is not null);
                File.WriteAllText(store.StatePath, "{"); Throws<JsonException>(() => store.Load());
                File.WriteAllText(store.StatePath, "{\"Schema\":55,\"Enabled\":true}"); Throws<InvalidDataException>(() => store.Load());
                await Task.CompletedTask;
            });
        });
        await Test("backup before mutation, restart idempotency, update reset repair and original restoration", async () => {
            await Temp(async store => {
                var adb = new FakeAdb(); var vm = adb.Add("127.0.0.1:16384", AppOpState.Default);
                adb.BeforeSet = () => { var s = store.Load(); Eq(AppOpState.Default, s.Backups.Values.Single().OriginalMode); };
                var state = new GuardState { Enabled = true };
                var engine = new ProtectionGuard(new(adb, _ => { }), store);
                var first = await engine.ReconcileAsync(adb.Serials, state); Eq(1, first.Repaired); Eq(1, adb.Sets);
                state = store.Load(); var afterRestart = await new ProtectionGuard(new(adb, _ => { }), store).ReconcileAsync(adb.Serials, state);
                Eq(0, afterRestart.Repaired); Eq(1, afterRestart.Verified); Eq(1, adb.Sets);
                vm.Mode = AppOpState.Allow; vm.Version = "99.17.2";
                var afterUpdate = await engine.ReconcileAsync(adb.Serials, state); Eq(1, afterUpdate.Repaired); Eq(2, adb.Sets);
                Eq(AppOpState.Default, state.Backups.Values.Single().OriginalMode);
                state.Enabled = false; store.Save(state); adb.BeforeSet = null;
                Eq(1, await engine.RestoreAsync(state)); Eq(AppOpState.Default, vm.Mode); Eq(0, state.Backups.Count);
            });
        });
        await Test("multi-instance isolates unknown response and newly added instance", async () => {
            await Temp(async store => {
                var adb = new FakeAdb(); adb.Add("127.0.0.1:16384", AppOpState.Unknown); adb.Add("127.0.0.1:16416", AppOpState.Allow);
                var state = new GuardState { Enabled = true };
                var s = await new ProtectionGuard(new(adb, _ => { }), store).ReconcileAsync(adb.Serials, state);
                Eq(1, s.Failed); Eq(1, s.Repaired); Eq(1, adb.Sets);
            });
        });
        await Test("deny mode is never weakened", async () => {
            await Temp(async store => {
                var adb = new FakeAdb(); adb.Add("127.0.0.1:16384", AppOpState.Deny);
                var s = await new ProtectionGuard(new(adb, _ => { }), store).ReconcileAsync(adb.Serials, new()); Eq(1, s.Verified); Eq(0, adb.Sets);
            });
        });
        await Test("missing Store and physical-device identity never mutated", async () => {
            var adb = new FakeAdb(); var vm = adb.Add("127.0.0.1:16384", AppOpState.Allow); vm.Store = false;
            var manager = new MuMuManager(adb, _ => { }); Yes(!(await manager.SetAppOpAsync(adb.Serials[0], "ignore")).ok);
            vm.Store = true; vm.Emulator = false; Yes(!(await manager.SetAppOpAsync(adb.Serials[0], "ignore")).ok); Eq(0, adb.Sets);
            Yes(await manager.InspectAsync("USB123") is null); Eq(0, adb.Sets);
        });
        await Test("zero-exit shell error and unapplied changes are failures", async () => {
            var adb = new FakeAdb(); adb.Add("127.0.0.1:16384", AppOpState.Allow);
            var manager = new MuMuManager(adb, _ => { }); adb.SetError = true;
            Yes(!(await manager.SetAppOpAsync(adb.Serials[0], "ignore")).ok);
            adb.SetError = false; adb.DropSet = true; Yes(!(await manager.SetAppOpAsync(adb.Serials[0], "ignore")).ok);
            await ThrowsAsync<ArgumentException>(() => manager.SetAppOpAsync(adb.Serials[0], "ignore;reboot"));
        });
        await Test("unknown identity prevents writes; restore mismatch retains backup", async () => {
            await Temp(async store => {
                var adb = new FakeAdb(); var vm = adb.Add("127.0.0.1:16384", AppOpState.Allow); vm.Id = "null";
                var state = new GuardState { Enabled = true }; var engine = new ProtectionGuard(new(adb, _ => { }), store);
                Eq(1, (await engine.ReconcileAsync(adb.Serials, state)).Failed); Eq(0, adb.Sets);
                vm.Id = "0123456789abcdef"; await engine.ReconcileAsync(adb.Serials, state); state.Enabled = false;
                vm.Id = "ffffffffffffffff"; Eq(0, await engine.RestoreAsync(state)); Eq(1, state.Backups.Count);
            });
        });
        await Test("round-robin cursor advances before a cancelled VM", async () => {
            await Temp(async store => {
                var adb = new FakeAdb(); adb.Add("127.0.0.1:16384", AppOpState.Allow); adb.Add("127.0.0.1:16416", AppOpState.Allow);
                adb.CancelSerial = adb.Serials[0]; var state = new GuardState();
                await ThrowsAsync<OperationCanceledException>(() => new ProtectionGuard(new(adb, _ => { }), store).ReconcileAsync(adb.Serials, state));
                Eq(1, store.Load().Cursor);
            });
        });
        await Test("Task XML escapes paths, least privilege and minute interval without expiry", () => {
            var xml = XDocument.Parse(GuardTask.BuildXml(@"C:\Users\A&B\Guard\MuMuAdBlocker.exe", "S-1-5-21-123", new DateTime(2026, 9, 19, 13, 0, 0)));
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            Eq("PT1M", xml.Descendants(ns + "Interval").Single().Value);
            Eq("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
            Eq("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
            Eq("IgnoreNew", xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
            Eq("--guard-once", xml.Descendants(ns + "Arguments").Single().Value);
            Yes(!xml.Descendants(ns + "EndBoundary").Any() && !xml.Descendants(ns + "Duration").Any());
            return Task.CompletedTask;
        });
        await Test("process arguments are literal and UTF-8 streams are preserved", async () => {
            var text = "한글 ; echo nope & \"quoted\"";
            var result = await AdbRunner.RunAsync(Environment.ProcessPath!, new[] { "--fixture-echo", text }, TimeSpan.FromSeconds(5));
            Eq(0, result.ExitCode); Eq(text, result.StdOut.Trim()); Eq("진단", result.StdErr.Trim());
        });
        await Test("process timeout and caller cancellation are distinct and bounded", async () => {
            var watch = Stopwatch.StartNew();
            await ThrowsAsync<TimeoutException>(() => AdbRunner.RunAsync(Environment.ProcessPath!, new[] { "--fixture-sleep" }, TimeSpan.FromMilliseconds(150)));
            using var cancel = new CancellationTokenSource(150);
            await ThrowsAsync<OperationCanceledException>(() => AdbRunner.RunAsync(Environment.ProcessPath!, new[] { "--fixture-sleep" }, TimeSpan.FromSeconds(5), cancel.Token));
            Yes(watch.Elapsed < TimeSpan.FromSeconds(5));
        });
        if (OperatingSystem.IsWindows()) await Test("native Windows Task Scheduler register/read/run/remove", WindowsTaskTest);
        else { _skipped++; Console.WriteLine("SKIP native Windows Task Scheduler (non-Windows host)"); }
        Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
        return _failed == 0 ? 0 : 1;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WindowsTaskTest()
    {
        var name = "MuMuAdBlocker-Test-" + Guid.NewGuid().ToString("N");
        object? service = null, root = null, task = null, running = null;
        try
        {
            Yes(GuardTask.ReadXml(name) is null); // COM interop maps ERROR_FILE_NOT_FOUND to FileNotFoundException.
            GuardTask.Delete(name); // Removing an absent task must also be idempotent.
            var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            GuardTask.Register(name, GuardTask.BuildXml(command, GuardTask.UserSid, DateTime.Now.AddYears(1), "/c exit 0"));
            Yes(GuardTask.ReadXml(name) is not null);
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ((dynamic)service).Connect(); root = ((dynamic)service).GetFolder("\\"); task = ((dynamic)root).GetTask(name);
            running = ((dynamic)task).Run(null);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(250);
                if ((int)((dynamic)task).State != 4 && (int)((dynamic)task).LastTaskResult == 0) break;
            }
            Eq(0, (int)((dynamic)task).LastTaskResult);
        }
        finally
        {
            foreach (var o in new[] { running, task, root, service }) if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
            GuardTask.Delete(name);
        }
        Yes(GuardTask.ReadXml(name) is null);
    }
    private static async Task Temp(Func<GuardStore, Task> test)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MuMuAdBlocker.Tests-" + Guid.NewGuid().ToString("N")));
        try { await test(new GuardStore(path)); }
        finally
        {
            if (Path.GetDirectoryName(path) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(path).StartsWith("MuMuAdBlocker.Tests-") && Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
    private static void Yes(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Eq<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected={expected}, actual={actual}"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Test(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + " :: " + ex); }
    }
}

internal sealed class FakeAdb : IAdbRunner
{
    internal sealed class Vm
    {
        public AppOpState Mode; public string Version = "6.4.7"; public bool Store = true, Emulator = true;
        public string Id = "0123456789abcdef";
    }
    private readonly Dictionary<string, Vm> _vms = new();
    public IReadOnlyList<string> Serials => _vms.Keys.ToArray();
    public int Sets; public bool SetError, DropSet; public string? CancelSerial; public Action? BeforeSet;
    public Vm Add(string serial, AppOpState mode) { var vm = new Vm { Mode = mode }; _vms.Add(serial, vm); return vm; }
    public Task<ProcessResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var a = args.ToArray();
        if (a[0] == "devices") return Ok("List of devices attached\n" + string.Join('\n', _vms.Keys.Select(s => s + "\tdevice")));
        if (a[0] == "connect") return Ok("connected");
        if (a[1] == CancelSerial) throw new OperationCanceledException();
        if (!_vms.TryGetValue(a[1], out var vm)) return Task.FromResult(new ProcessResult(1, "", "offline"));
        var cmd = a[3];
        if (cmd.StartsWith("pm path")) return Ok(vm.Store ? "package:/system/app/MuMuStore.apk" : "");
        if (cmd == "getprop") return Ok(vm.Emulator ? "[ro.kernel.qemu]: [1]\n[ro.product.model]: [MuMu]" : "[ro.product.model]: [Pixel 9]");
        if (cmd.StartsWith("dumpsys package")) return Ok("versionName=" + vm.Version + "\nversionCode=42");
        if (cmd.Contains("appops get")) return Ok(vm.Mode == AppOpState.Unknown ? "Error: unknown future format" : "SYSTEM_ALERT_WINDOW: " + vm.Mode.ToString().ToLowerInvariant());
        if (cmd.Contains("appops set"))
        {
            BeforeSet?.Invoke(); Sets++;
            if (SetError) return Ok("Error: permission denied");
            if (!DropSet) vm.Mode = Enum.Parse<AppOpState>(cmd.Split(' ').Last(), true);
            return Ok("");
        }
        if (cmd.StartsWith("settings --user 0")) return Ok(vm.Id);
        throw new Exception("Unexpected ADB command: " + string.Join(' ', a));
    }
    private static Task<ProcessResult> Ok(string text) => Task.FromResult(new ProcessResult(0, text, ""));
}
