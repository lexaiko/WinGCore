param([switch]$Live)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$runtimeExe = Join-Path $workspace 'src/GManager.RuntimeHost/bin/Debug/net8.0-windows/GManager.RuntimeHost.exe'
if (-not (Test-Path -LiteralPath $runtimeExe)) { throw 'Build GManager.RuntimeHost first.' }
$runDirectory = Join-Path $workspace ('Temp/runtime-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$runtimeProcess = $null

function Start-Runtime {
    $process = Start-Process -FilePath $runtimeExe -ArgumentList @('serve', '--data-dir', ('"' + $runDirectory + '"')) -WindowStyle Hidden -PassThru
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        if ($process.HasExited) { throw 'Runtime exited during startup.' }
        $raw = & $runtimeExe status --data-dir $runDirectory 2>$null
        if ($LASTEXITCODE -eq 0) { return $process }
        Start-Sleep -Milliseconds 100
    }
    $process.Kill()
    throw 'Runtime did not become ready.'
}

function Invoke-Runtime([string[]]$CommandArguments) {
    $raw = & $runtimeExe @CommandArguments --data-dir $runDirectory
    if (-not $raw) { throw 'Runtime returned no JSON.' }
    return ($raw | ConvertFrom-Json)
}

try {
    $runtimeProcess = Start-Runtime
    $created = Invoke-Runtime @('create', (Join-Path $workspace 'profiles/windows-lab.json'))
    if (-not $created.success) { throw 'Device creation failed.' }
    $deviceId = $created.devices[0].id
    $first = $null
    if ($Live) {
        $first = Invoke-Runtime @('checkin', $deviceId)
        Write-Output ('First check-in: ' + $first.code + ' / ' + $first.message)
    }
    $null = Invoke-Runtime @('stop')
    if (-not $runtimeProcess.WaitForExit(5000)) { throw 'Runtime did not stop.' }
    $runtimeProcess = Start-Runtime
    $listed = Invoke-Runtime @('list')
    if ($listed.devices.Count -ne 1 -or $listed.devices[0].id -ne $deviceId) { throw 'Device identity did not persist.' }
    Write-Output 'Local identity survived process restart.'
    $second = $null
    if ($Live -and $first.success) {
        $second = Invoke-Runtime @('checkin', $deviceId)
        Write-Output ('Second check-in: ' + $second.code + ' / ' + $second.message)
        if ($second.success -and $first.devices[0].googleAndroidId -ne $second.devices[0].googleAndroidId) {
            throw 'Google device ID changed after restart.'
        }
        if ($second.success) { Write-Output 'Google registration ID survived process restart and repeat check-in.' }
    }
    $report = [ordered]@{
        utc = [DateTimeOffset]::UtcNow.ToString('O')
        live = [bool]$Live
        localIdentityPersisted = $true
        firstCheckin = if ($first) { $first.code } else { 'NotRun' }
        repeatCheckin = if ($second) { $second.code } else { 'NotRun' }
        sameGoogleId = if ($second -and $second.success) { $first.devices[0].googleAndroidId -eq $second.devices[0].googleAndroidId } else { $null }
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'result.json') -Encoding utf8
    Write-Output ('Report: ' + (Join-Path $runDirectory 'result.json'))
    if ($Live -and (-not $first.success -or -not $second -or -not $second.success)) { throw 'Live check-in was not fully verified; see report.' }
}
finally {
    if ($runtimeProcess -and -not $runtimeProcess.HasExited) {
        try { $null = Invoke-Runtime @('stop') } catch { }
        if (-not $runtimeProcess.WaitForExit(5000)) { $runtimeProcess.Kill() }
    }
}
