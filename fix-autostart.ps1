param(
    [string]$Target = 'D:\SoftWare\CpuGlassMonitor\CpuGlassMonitor.exe'
)

$ErrorActionPreference = 'Stop'
$targetDir = Split-Path $Target -Parent
$result   = Join-Path $PSScriptRoot 'fix-result.txt'
$appLog   = Join-Path $env:APPDATA 'CpuGlassMonitor\app.log'
$dirLog   = Join-Path $targetDir 'app.log'
$svcName  = 'R0CpuGlassMonitor'
$out = @()
try {
    $out += '=== migrate run: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ==='
    $out += 'target = ' + $Target
    if (-not (Test-Path $Target)) { throw "missing target exe: $Target" }

    # 1. Point the logon task at the new install directory.
    $a = New-ScheduledTaskAction -Execute $Target -Argument '--autostart'
    Set-ScheduledTask -TaskName CpuGlassMonitor -Action $a | Out-Null
    $out += 'OK task action = ' + (Get-ScheduledTask -TaskName CpuGlassMonitor).Actions[0].Execute

    # 2. Rebuild shortcuts. The old start-menu one still points at the dist exe.
    #    RunAsUser makes double-click start elevated directly (no extra relaunch).
    try {
        $sh = New-Object -ComObject WScript.Shell
        $dirs = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))
        foreach ($d in $dirs) {
            $lnk = Join-Path $d 'CpuGlassMonitor.lnk'
            $s = $sh.CreateShortcut($lnk)
            $s.TargetPath = $Target
            $s.Arguments = ''
            $s.WorkingDirectory = $targetDir
            $s.IconLocation = $Target + ',0'
            $s.Description = 'CpuGlassMonitor hardware monitor'
            $s.Save()
            $b = [IO.File]::ReadAllBytes($lnk)
            $b[21] = $b[21] -bor 0x20
            [IO.File]::WriteAllBytes($lnk, $b)
            $out += 'OK shortcut = ' + $lnk
        }
    } catch {
        $out += 'WARN shortcuts: ' + $_.Exception.Message
    }

    # 3. Stop the running instance and drop the stale kernel service so the
    #    relaunched instance registers it against the new directory.
    Get-Process -Name CpuGlassMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $svcName 2>&1 | Out-Null
    $out += 'driver service cleanup: delete exit=' + $LASTEXITCODE

    # 4. Trigger now; no need to wait for the next logon to verify.
    Start-ScheduledTask -TaskName CpuGlassMonitor
    $out += 'OK task triggered (checking result in 14s)'
    Start-Sleep -Seconds 14

    $p = @(Get-Process -Name CpuGlassMonitor -ErrorAction SilentlyContinue)
    if ($p.Count -gt 0) {
        foreach ($x in $p) { $out += ('RUNNING pid=' + $x.Id) }
    } else {
        $out += 'WARN: no CpuGlassMonitor process after trigger'
    }

    # 5. Report. app.log is UTF-8 without BOM; PS 5.1 defaults to ANSI,
    #    so -Encoding UTF8 is required or every Chinese line turns to mojibake.
    $logLines = @()
    if (Test-Path $appLog) {
        $logLines = @(Get-Content $appLog -Tail 40 -Encoding UTF8)
        $out += '---- APPDATA app.log (last 30 lines, UTF-8) ----'
        $out += @($logLines | Select-Object -Last 30)
    } else {
        $out += 'WARN: no appdata log (process likely NOT elevated)'
    }
    $joined = $logLines -join "`n"
    $out += '---- VERDICT ----'
    $out += 'token_usable      = ' + $(if ($joined -match 'usable=True') { 'PASS' } else { 'FAIL' })
    $out += 'cpu_temperature   = ' + $(if ($joined -match 'cpuTemp=True') { 'PASS' } else { 'FAIL' })
    $out += 'tray_promoted     = ' + $(if ($joined -match 'IsPromoted=1') { 'PASS' } else { 'FAIL' })
    $out += 'engine_ready      = ' + $(if ($joined -match 'elevated=True') { 'PASS' } else { 'FAIL' })

    $image = ''
    $svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $svcName
    if (Test-Path $svcKey) { $image = (Get-ItemProperty $svcKey).ImagePath }
    $clean = $image -and ($image -like "*$targetDir*")
    $out += 'driver_image      = ' + $(if ($clean) { 'PASS (new dir)' } else { 'WARN (stale: ' + $image + ')' })
    $out += 'driver_image_path = ' + $image

    if (Test-Path $dirLog) {
        $out += '---- install-dir app.log (low-IL fallback, last 15 lines, UTF-8) ----'
        $out += @(Get-Content $dirLog -Tail 15 -Encoding UTF8)
    }
} catch {
    $out += 'ERROR: ' + $_.Exception.Message
}
$out | Out-File -FilePath $result -Encoding utf8
