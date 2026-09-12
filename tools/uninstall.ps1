<#
    Removes everything Claude Watch put on this machine: shortcuts, the
    start-with-Windows entry and the two scheduled tasks. Settings and the
    activity log stay unless you say otherwise.
#>

$ErrorActionPreference = 'SilentlyContinue'

Write-Host ''
Write-Host '  Claude Watch — removal' -ForegroundColor White
Write-Host ''

Get-Process -Name 'ClaudeWatch' | Stop-Process -Force
Start-Sleep -Milliseconds 400

foreach ($folder in @(
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
    Remove-Item (Join-Path $folder 'Claude Watch.lnk') -Force
}
Write-Host '  Shortcuts removed.' -ForegroundColor Gray

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClaudeWatch' -Force
Write-Host '  Start-with-Windows entry removed.' -ForegroundColor Gray

$tasks = @('SafeChat-SetWorkTimeZone', 'SafeChat-SetHomeTimeZone')
$present = $tasks | Where-Object { schtasks /query /tn $_ 2>$null; $LASTEXITCODE -eq 0 }

if ($present) {
    Write-Host '  Removing the scheduled tasks needs one approval prompt.' -ForegroundColor Yellow
    $script = Join-Path $env:TEMP ('claude-watch-remove-' + [guid]::NewGuid().ToString('N') + '.cmd')
    "@echo off`r`n" + (($tasks | ForEach-Object { "schtasks /delete /f /tn `"$_`"" }) -join "`r`n") |
        Set-Content $script -Encoding ASCII
    Start-Process -FilePath $script -Verb RunAs -WindowStyle Hidden -Wait
    Remove-Item $script -Force
    Write-Host '  Scheduled tasks removed.' -ForegroundColor Gray
}

$data = Join-Path $env:APPDATA 'ClaudeWatch'
if (Test-Path $data) {
    Write-Host ''
    $answer = Read-Host '  Also delete your settings and activity log? (y/N)'
    if ($answer -eq 'y') {
        Remove-Item $data -Recurse -Force
        Write-Host '  Data folder deleted.' -ForegroundColor Gray
    }
    else {
        Write-Host "  Kept: $data" -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host '  Done. You can delete this folder now.' -ForegroundColor Green
Write-Host ''
Read-Host '  Press Enter to close'
