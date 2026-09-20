param([Parameter(Mandatory=$true)][string]$ExePath, [string]$PreviousExePath, [string]$EvidencePath = (Join-Path $PWD 'guard-evidence.json'))
$ErrorActionPreference = 'Stop'
$published = (Resolve-Path -LiteralPath $ExePath).Path
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MuMuAdBlocker'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MuMuAdBlocker.lnk'
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$taskName = "MuMuAdBlocker-Guard-$($identity.User.Value)"
# Isolated Windows CI only. Never weaken these real-data guards.
if (Test-Path -LiteralPath $root) { throw "Refusing pre-existing data: $root" }
if (Test-Path -LiteralPath $shortcut) { throw 'Refusing pre-existing management shortcut' }
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Refusing existing guard task' }
if (Get-Process -Name MuMuPlayer,MuMuNxDevice,MuMuNxMain,NemuPlayer,MuMuVMHeadless,MuMuVMMHeadless,MuMuAndroidDevice -ErrorAction SilentlyContinue) { throw 'Refusing running MuMu installation' }
$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('MuMuAdBlocker.DownloadTest-' + [guid]::NewGuid().ToString('N'))))
$download = Join-Path $fixture 'download'
$expectedFixture = $fixture
$expectedDownload = [IO.Path]::GetFullPath($download)
$exe = Join-Path $download 'MuMuAdBlocker.exe'
$statePath = Join-Path $root 'guard.json'
$statusPath = Join-Path $root 'guard-status.json'
$evidence = [ordered]@{
    Account = $identity.Name
    ElevatedAdministrator = ([System.Security.Principal.WindowsPrincipal]$identity).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    OS = [Environment]::OSVersion.VersionString
    SourceDeleted = $false; AutomaticRuns = @(); Checks = @()
    WindowObservation = 'MainWindowHandle polling (100-500 ms); no interactive desktop screenshot'
}
function Check([string]$Name) { $evidence.Checks += $Name; Write-Output "PASS $Name" }
function Assert-NoGuardWindow {
    foreach ($p in @(Get-Process -Name MuMuAdBlocker -ErrorAction SilentlyContinue)) {
        if ($p.MainWindowHandle -ne 0) { throw "Unexpected guard window: $($p.Id)" }
    }
}
function Run-Exe([string]$Path, [string]$Argument, [int]$Expected = 0) {
    $p = Start-Process -FilePath $Path -ArgumentList $Argument -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(60)
    while (-not $p.WaitForExit(100)) {
        Assert-NoGuardWindow
        if ((Get-Date) -ge $deadline) { $p.Kill(); throw "CLI timed out: $Argument" }
    }
    if ($p.ExitCode -ne $Expected) {
        if (Test-Path -LiteralPath (Join-Path $root 'guard.log')) { Get-Content -LiteralPath (Join-Path $root 'guard.log') -Tail 35 | Write-Output }
        throw "CLI $Argument returned $($p.ExitCode), expected $Expected"
    }
}
function Read-Status { Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json }
function Wait-TaskRun([datetime]$AfterRun, [datetimeoffset]$AfterCheck, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 500
        Assert-NoGuardWindow
        $info = Get-ScheduledTaskInfo -TaskName $taskName
        $current = Get-ScheduledTask -TaskName $taskName
        $status = Read-Status
        if ($info.LastRunTime -gt $AfterRun -and $current.State -ne 'Running' -and ([datetimeoffset]$status.CheckedUtc) -gt $AfterCheck) {
            if ($info.LastTaskResult -ne 0 -or $status.State -ne '대기') { throw "Guard failure: $($info.LastTaskResult), $($status.State)" }
            return $info
        }
    } while ((Get-Date) -lt $deadline)
    throw 'No completed task run with fresh status timestamp'
}
function Assert-OldState([string]$TaskExe, [string]$StateHash) {
    if ((Get-ScheduledTask -TaskName $taskName).Actions.Execute -ne $TaskExe) { throw 'Failed update changed old task' }
    if ((Get-FileHash -LiteralPath $statePath).Hash -ne $StateHash) { throw 'Failed update changed old settings/backups' }
}
try {
    New-Item -ItemType Directory -Path $download | Out-Null
    Copy-Item -LiteralPath $published -Destination $exe
    Run-Exe $exe '--smoke-test'
    if ($PreviousExePath) {
        Run-Exe (Resolve-Path -LiteralPath $PreviousExePath).Path '--install-guard'
        $oldTaskExe = (Get-ScheduledTask -TaskName $taskName).Actions.Execute
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $state.AdbPath = 'C:\MuMuAdBlocker-Test-Missing\adb.exe'
        $state.Endpoints = @('127.0.0.1:16416')
        $state.Backups | Add-Member -NotePropertyName 'fixture-backup' -NotePropertyValue @{
            Serial='127.0.0.1:16416'; AndroidId='0123456789abcdef'; StoreVersion='fixture'; OriginalMode=2; CreatedUtc='2026-09-19T00:00:00Z'
        }
        $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statePath -Encoding utf8
        @{AdbPath=$state.AdbPath; LastEndpoint='127.0.0.1:16416'; LastDevice='fixture'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'settings.json') -Encoding utf8
        $oldHash = (Get-FileHash -LiteralPath $statePath).Hash
        $newHash = (Get-FileHash -LiteralPath $exe).Hash
        $targetDir = Join-Path (Join-Path $root 'guard-bin') $newHash.Substring(0,16)
        New-Item -ItemType Directory -Path $targetDir | Out-Null
        $target = Join-Path $targetDir 'MuMuAdBlocker.exe'
        [IO.File]::WriteAllText($target, 'corrupt test copy')
        Run-Exe $exe '--install-guard' 1
        Assert-OldState $oldTaskExe $oldHash
        Remove-Item -LiteralPath $target
        Check 'corrupt copy rejects update, preserving old task/settings/backups'
        New-Item -ItemType Directory -Path ($target + '.tmp') | Out-Null
        Run-Exe $exe '--install-guard' 1
        Assert-OldState $oldTaskExe $oldHash
        Remove-Item -LiteralPath ($target + '.tmp')
        Check 'interrupted copy rejects update, preserving old state'
    }
    Run-Exe $exe '--install-guard'
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    if ($task.Principal.RunLevel -ne 'Limited' -or $task.Actions.Arguments -ne '--guard-once') { throw 'Wrong guard principal/action' }
    $copy = $task.Actions.Execute
    $copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
    if ($copyHash -ne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash) { throw 'Copy checksum mismatch' }
    if ($task.Actions.WorkingDirectory -ne (Split-Path -Parent $copy)) { throw 'Wrong working directory' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if (-not $state.Enabled) { throw 'Guard not enabled' }
    if ($PreviousExePath) {
        if ($state.Backups.'fixture-backup'.StoreVersion -ne 'fixture' -or $state.AdbPath -ne 'C:\MuMuAdBlocker-Test-Missing\adb.exe') { throw 'Upgrade lost configuration/backup' }
        Check 'v1.1.0 upgrade preserves settings and legacy backup'
    }
    $beforeState = (Get-FileHash -LiteralPath $statePath).Hash
    Run-Exe $exe '--install-guard'
    if ((Get-FileHash -LiteralPath $statePath).Hash -ne $beforeState) { throw 'Repeated install changed settings' }
    Check 'same-version reinstall is idempotent'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    if ($link.TargetPath -ne $copy -or $link.Arguments -ne '' -or $link.WorkingDirectory -ne (Split-Path -Parent $copy)) { throw 'Shortcut depends on source' }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    # Only our verified unique fixture is deleted; published EXE and user Downloads are untouched.
    if ([IO.Path]::GetFullPath($download) -ne $expectedDownload -or (Split-Path -Parent $download) -ne $expectedFixture) { throw 'Unsafe deletion path' }
    Remove-Item -LiteralPath $download -Recurse -Force
    if (Test-Path -LiteralPath $download) { throw 'Download origin still exists' }
    $evidence.SourceDeleted = $true
    $evidence.InstalledSha256 = $copyHash.ToLowerInvariant()
    Run-Exe $copy '--smoke-test'
    Check 'download EXE/directory actually deleted; installed EXE and management shortcut independent'
    $before = Get-ScheduledTaskInfo -TaskName $taskName
    $status = Read-Status
    Start-ScheduledTask -TaskName $taskName
    $manual = Wait-TaskRun $before.LastRunTime ([datetimeoffset]$status.CheckedUtc) 30
    Check 'manual trigger after source deletion completes with fresh status'
    # No manual trigger here: observe the unchanged PT1M trigger twice.
    $lastRun = $manual.LastRunTime
    for ($i=0; $i -lt 2; $i++) {
        $status = Read-Status
        $auto = Wait-TaskRun $lastRun ([datetimeoffset]$status.CheckedUtc) 95
        $evidence.AutomaticRuns += $auto.LastRunTime.ToUniversalTime().ToString('O')
        $lastRun = $auto.LastRunTime
    }
    Check 'two actual minute-trigger runs after deletion, without GUI/manual invocation'
    if (Get-Process -Name adb,adb_server,MuMuPlayer,MuMuNxDevice,NemuPlayer -ErrorAction SilentlyContinue) { throw 'Unexpected ADB/emulator startup' }
    Check 'MuMu absent: no ADB/emulator started'
    Run-Exe $copy '--remove-guard'
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Task remained after removal' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.Enabled -or -not (Test-Path -LiteralPath (Join-Path $root 'guard.disabled'))) { throw 'Guard still enabled' }
    if ($PreviousExePath -and $state.Backups.'fixture-backup'.StoreVersion -ne 'fixture') { throw 'Disable lost backup' }
    # Clear only the synthetic backup to test no-backup restore entry without starting ADB.
    $state.Backups = @{}
    $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statePath -Encoding utf8
    Run-Exe $copy '--restore-saved'
    Run-Exe $copy '--guard-once'
    Check 'installed copy can disable and enter original restoration after source deletion'
    Run-Exe $copy '--install-guard'
    [IO.File]::WriteAllText($statePath, '{corrupt-fixture')
    $corruptHash = (Get-FileHash -LiteralPath $statePath).Hash
    Run-Exe $copy '--guard-once' 1
    Run-Exe $copy '--remove-guard'
    Run-Exe $copy '--guard-once'
    if ((Get-FileHash -LiteralPath $statePath).Hash -ne $corruptHash) { throw 'Damaged backup overwritten' }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Corrupt-state disable left task' }
    Check 'corrupt settings fail closed; disable preserves exact damaged bytes'
    $evidence.Result = 'passed'
}
finally {
    if (Test-Path -LiteralPath (Join-Path $root 'guard.log')) { $evidence.GuardLog = @(Get-Content -LiteralPath (Join-Path $root 'guard.log') -Tail 50) }
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $shortcut -PathType Leaf) { Remove-Item -LiteralPath $shortcut -Force }
    $expected = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MuMuAdBlocker'))
    if ([IO.Path]::GetFullPath($root) -eq $expected -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
    if ([IO.Path]::GetFullPath($fixture) -eq $expectedFixture -and (Split-Path -Parent $fixture) -eq ([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')) -and (Test-Path -LiteralPath $fixture)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
