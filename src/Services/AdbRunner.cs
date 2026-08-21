using System.Diagnostics;
using System.Text;

namespace MuMuAdBlocker;

/// <summary>외부 프로세스 실행 결과</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + Environment.NewLine + StdErr;
}

/// <summary>
/// adb.exe 실행을 담당. ArgumentList를 사용해 인자 조립/인젝션 문제를 방지한다.
/// </summary>
public sealed class AdbRunner
{
    public string AdbPath { get; }
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public AdbRunner(string adbPath)
    {
        if (string.IsNullOrWhiteSpace(adbPath)) throw new ArgumentException("adb 경로가 비어 있습니다.", nameof(adbPath));
        AdbPath = adbPath;
    }

    /// <summary>adb version 실행으로 실제 동작 가능 여부 검증</summary>
    public static async Task<(bool ok, string message)> ValidateAsync(string adbPath)
    {
        try
        {
            var r = await RunAsync(adbPath, new[] { "version" }, TimeSpan.FromSeconds(10));
            if (r.Success && r.StdOut.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase))
                return (true, r.StdOut.Trim());
            if (r.ExitCode < 0)
                return (false, "선택한 adb.exe를 실행할 수 없습니다.\n필요한 DLL 또는 Platform Tools 파일이 누락되었을 수 있습니다.\n" + r.StdErr.Trim());
            return (false, "선택한 파일은 정상적인 adb.exe가 아닙니다.\n" + r.Combined.Trim());
        }
        catch (Exception ex)
        {
            return (false, "선택한 adb.exe를 실행할 수 없습니다.\n" + ex.Message);
        }
    }

    public Task<ProcessResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
        => RunAsync(AdbPath, args, timeout ?? DefaultTimeout, ct);

    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        var waitTask = proc.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout, ct));
        if (completed != waitTask || !proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"명령 실행이 {timeout.TotalSeconds:0}초 내에 완료되지 않았습니다: {Path.GetFileName(exe)}");
        }

        string so, se;
        try { so = await stdoutTask; se = await stderrTask; }
        catch { so = string.Empty; se = string.Empty; }
        return new ProcessResult(proc.ExitCode, so ?? string.Empty, se ?? string.Empty);
    }
}
