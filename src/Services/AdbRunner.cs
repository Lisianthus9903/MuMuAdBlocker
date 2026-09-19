using System.Diagnostics;
using System.Text;

namespace MuMuAdBlocker;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + Environment.NewLine + StdErr;
}

public interface IAdbRunner
{
    Task<ProcessResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default);
}

public sealed class AdbRunner : IAdbRunner
{
    public string AdbPath { get; }
    public AdbRunner(string adbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        AdbPath = adbPath;
    }

    public static async Task<(bool ok, string message)> ValidateAsync(string adbPath)
    {
        try
        {
            var r = await RunAsync(adbPath, new[] { "version" }, TimeSpan.FromSeconds(5));
            return (r.Success && r.StdOut.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase), r.Combined.Trim());
        }
        catch (Exception ex) { return (false, "ADB 실행 실패: " + ex.Message); }
    }

    public Task<ProcessResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
        => RunAsync(AdbPath, args, timeout ?? TimeSpan.FromSeconds(8), ct);

    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await proc.WaitForExitAsync(deadline.Token);
            var streams = await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(deadline.Token);
            return new(proc.ExitCode, streams[0], streams[1]);
        }
        catch (OperationCanceledException)
        {
            // Stop only this client, not the shared ADB server or emulator/game processes.
            try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"명령 실행 시간 초과 ({timeout.TotalSeconds:0}초): {Path.GetFileName(exe)}");
        }
    }
}
