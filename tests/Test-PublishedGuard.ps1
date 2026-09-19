param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MuMuAdBlocker'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "MuMuAdBlocker-Guard-$sid"
# This integration test is for an isolated Windows CI runner, never an existing user installation.
if (Test-Path -LiteralPath $root) { throw "Refusing to touch pre-existing data: $root" }
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Refusing to replace an existing guard task' }
if (Get-Process -Name MuMuPlayer,MuMuNxDevice,NemuPlayer -ErrorAction SilentlyContinue) { throw 'Refusing integration test on a running MuMu installation' }
function Run-Exe([string]$Path, [string]$Argument) {
    $process = Start-Process -FilePath $Path -ArgumentList $Argument -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "CLI timed out: $Argument" }
    if ($process.ExitCode -ne 0) { throw "CLI $Argument failed: $($process.ExitCode)" }
}
try {
    Run-Exe $exe '--smoke-test'
    Run-Exe $exe '--install-guard'
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    if ($task.Principal.RunLevel -ne 'Limited') { throw 'Guard is not least-privileged' }
    if ($task.Actions.Arguments -ne '--guard-once') { throw 'Wrong guard action' }
    $copy = $task.Actions.Execute
    if (-not (Test-Path -LiteralPath $copy)) { throw 'Installed copy missing' }
    if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash) { throw 'Installed copy checksum mismatch' }
    $state = Get-Content -LiteralPath (Join-Path $root 'guard.json') -Raw | ConvertFrom-Json
    if (-not $state.Enabled) { throw 'Guard not enabled after install' }
    $status = Get-Content -LiteralPath (Join-Path $root 'guard-status.json') -Raw | ConvertFrom-Json
    if ($status.State -ne '대기') { throw 'Absent MuMu should produce idle status' }
    # Installation/update must be idempotent and preserve the stable verified copy.
    Run-Exe $exe '--install-guard'
    Run-Exe $copy '--smoke-test'
    $before = (Get-ScheduledTaskInfo -TaskName $taskName).LastRunTime
    Start-ScheduledTask -TaskName $taskName
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $info = Get-ScheduledTaskInfo -TaskName $taskName
        $current = Get-ScheduledTask -TaskName $taskName
    } while (($info.LastRunTime -le $before -or $current.State -eq 'Running') -and (Get-Date) -lt $deadline)
    if ($info.LastRunTime -le $before -or $info.LastTaskResult -ne 0) { throw "Installed scheduled guard did not complete successfully: $($info.LastTaskResult)" }
    Run-Exe $exe '--remove-guard'
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Guard task remained after removal' }
    $state = Get-Content -LiteralPath (Join-Path $root 'guard.json') -Raw | ConvertFrom-Json
    if ($state.Enabled) { throw 'Guard remained enabled after removal' }
    Run-Exe $copy '--guard-once'
    Write-Output 'PASS published EXE: install, checksum, idempotent update, native scheduled execution, disable, disabled no-op'
}
finally {
    # Remove only the task and directory created by this test; the existence guards above are mandatory.
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    $expected = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MuMuAdBlocker'))
    if ([IO.Path]::GetFullPath($root) -eq $expected -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
