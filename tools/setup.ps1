<#
    SafeChat — first-run setup.

    Finds or installs the .NET SDK, builds the app, makes Start Menu and Desktop
    shortcuts, and leaves a shareable zip in beta\. Run it once; after that the
    app is just an icon.

        -SelfContained   also build a copy that runs with nothing installed
#>

[CmdletBinding()]
param(
    [switch]$SelfContained,

    # Rebuild in place after a code change: no zip, no fuss, app restarted.
    [switch]$Rebuild,

    # Reports what SDK it can see and stops. Used by the project's own checks.
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logFile = Join-Path $root 'setup-log.txt'
$script:Dotnet = $null

if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }

function Say([string]$text, [string]$colour = 'Gray') {
    Write-Host "  $text" -ForegroundColor $colour
}

function Log([string]$text) {
    try { Add-Content -Path $logFile -Value $text -Encoding utf8 } catch { }
}

function Fail([string]$text) {
    Write-Host ''
    Say $text 'Red'
    Write-Host ''
    Say 'The full log is in setup-log.txt next to this file.' 'DarkGray'
    Write-Host ''
    Read-Host '  Press Enter to close'
    exit 1
}

Write-Host ''
Write-Host '  SafeChat' -ForegroundColor White
if ($Rebuild) {
    Write-Host '  Rebuilding in place' -ForegroundColor DarkGray
}
else {
    Write-Host '  First-time setup' -ForegroundColor DarkGray
}
Write-Host ''

function Stop-App {
    $running = Get-Process -Name 'SafeChat' -ErrorAction SilentlyContinue
    if (-not $running) { return $false }

    Say 'Closing the running app so its files can be replaced.' 'DarkGray'
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700
    return $true
}

# ============================================================== the .NET SDK

function Refresh-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = ($machine, $user | Where-Object { $_ }) -join ';'
}

function Get-DotnetCandidates {
    $paths = @()

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $paths += $command.Source }

    # An environment variable can be missing, so every one is checked before use.
    foreach ($pair in @(
        @($env:ProgramFiles, 'dotnet\dotnet.exe'),
        @($env:LOCALAPPDATA, 'Microsoft\dotnet\dotnet.exe'),
        @($env:DOTNET_ROOT, 'dotnet.exe'))) {

        if ($pair[0]) { $paths += (Join-Path $pair[0] $pair[1]) }
    }

    $paths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
}

function Get-SdkMajors([string]$exe) {
    $majors = @()

    try {
        $lines = & $exe --list-sdks 2>$null
    }
    catch {
        return @()
    }

    foreach ($line in $lines) {
        if ($line -match '^\s*(\d+)\.') { $majors += [int]$Matches[1] }
    }

    return @($majors | Sort-Object -Unique)
}

function Find-Sdk {
    foreach ($exe in Get-DotnetCandidates) {
        $majors = Get-SdkMajors $exe
        if ($majors.Count -gt 0) {
            return [pscustomobject]@{ Exe = $exe; Majors = $majors }
        }
    }

    return $null
}

function Install-WithWinget {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Log 'winget is not on this machine'
        return $false
    }

    Say 'Installing the .NET 8 SDK with winget. This takes a few minutes.' 'Yellow'

    try {
        $output = winget install --id Microsoft.DotNet.SDK.8 --exact --silent `
            --accept-package-agreements --accept-source-agreements 2>&1
        Log ($output | Out-String)
    }
    catch {
        Log "winget failed: $_"
        return $false
    }

    Refresh-Path
    return $null -ne (Find-Sdk)
}

function Install-WithScript {
    # Microsoft's own installer. It puts the SDK under the user's own folder, so
    # it needs no administrator rights and cannot disturb an existing install.
    Say "Installing the .NET 8 SDK from Microsoft's installer. This takes a few minutes." 'Yellow'

    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    $target = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    }
    catch {
        Log "could not download dotnet-install.ps1: $_"
        return $false
    }

    try {
        & $installer -Channel 8.0 -InstallDir $target -NoPath 2>&1 | Tee-Object -Variable output | Out-Null
        Log ($output | Out-String)
    }
    catch {
        Log "dotnet-install failed: $_"
        return $false
    }

    $env:DOTNET_ROOT = $target
    Refresh-Path
    return $null -ne (Find-Sdk)
}

$sdk = Find-Sdk

if ($CheckOnly) {
    if ($sdk) {
        Write-Output ("SDK " + (($sdk.Majors | ForEach-Object { "$_.0" }) -join ',') + " at " + $sdk.Exe)
        exit 0
    }

    $found = Get-DotnetCandidates
    if ($found) { Write-Output 'RUNTIME-ONLY' } else { Write-Output 'NONE' }
    exit 2
}

if (-not $sdk) {
    $found = Get-DotnetCandidates
    if ($found) {
        Say 'dotnet is here but only as a runtime. The SDK is what builds the app.' 'Yellow'
    }
    else {
        Say 'The .NET SDK is not on this machine.' 'Yellow'
    }

    $installed = Install-WithWinget
    if (-not $installed) { $installed = Install-WithScript }

    if (-not $installed) {
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/8.0'
        Fail 'The SDK could not be installed automatically. Install it from the page that just opened, then run this again.'
    }

    $sdk = Find-Sdk
    if (-not $sdk) {
        Fail 'The SDK was installed but cannot be found. Restart the computer and run this again.'
    }

    Say 'The .NET SDK is installed.' 'Green'
}

$script:Dotnet = $sdk.Exe
Say ("Using .NET SDK " + (($sdk.Majors | ForEach-Object { "$_.0" }) -join ', ') + " at $($sdk.Exe)") 'DarkGray'

# ============================================================== target frame

# The projects target .NET 8. With only a newer SDK present, retarget them so the
# build never has to reach the internet for an older reference pack.
$target = if ($sdk.Majors -contains 8) { 8 } else { ($sdk.Majors | Measure-Object -Maximum).Maximum }

if ($target -ne 8) {
    Say "Retargeting the project to .NET $target so it builds with what you have." 'Yellow'

    foreach ($project in @(
        'src\ClaudeWatch.App\ClaudeWatch.App.csproj',
        'src\ClaudeWatch.Core\ClaudeWatch.Core.csproj',
        'server\ClaudeWatch.Orders.csproj',
        'tests\ClaudeWatch.Tests\ClaudeWatch.Tests.csproj')) {

        $path = Join-Path $root $project
        if (Test-Path $path) {
            (Get-Content $path -Raw).Replace('net8.0', "net$target.0") | Set-Content $path -NoNewline
        }
    }
}

# =================================================================== building

$project = Join-Path $root 'src\ClaudeWatch.App\ClaudeWatch.App.csproj'
$output = Join-Path $root 'build\app'
$config = Join-Path $root 'nuget.config'
$configBackup = "$config.online-fallback"

function Invoke-Publish([string[]]$extra) {
    $arguments = @('publish', $project, '-c', 'Release', '-o', $output, '--nologo') + $extra
    $text = & $script:Dotnet @arguments 2>&1
    Log ($text | Out-String)
    return @{ Ok = ($LASTEXITCODE -eq 0); Text = $text }
}

# A running copy holds its own files open, which would fail the publish.
$wasRunning = Stop-App

if ($Rebuild) {
    Say 'Building.' 'Gray'
}
else {
    Say 'Building. The first run takes a minute.' 'Gray'
}

$result = Invoke-Publish @()

if (-not $result.Ok) {
    # The offline package source is there so a machine with no internet can still
    # build. If a reference pack turns out to be missing, allow the normal source
    # for one retry rather than stopping.
    Say 'Retrying with the online package source.' 'Yellow'

    try {
        if (Test-Path $config) { Move-Item $config $configBackup -Force }
        $result = Invoke-Publish @()
    }
    finally {
        if (Test-Path $configBackup) { Move-Item $configBackup $config -Force }
    }
}

$exe = Join-Path $output 'SafeChat.exe'

if (-not $result.Ok -or -not (Test-Path $exe)) {
    $result.Text | Select-Object -Last 25 | ForEach-Object { Say $_ 'DarkGray' }
    Fail 'The build did not finish.'
}

Say 'Built.' 'Green'

# ============================================================== the beta zip

if (-not $Rebuild) {
try {
    $betaFolder = Join-Path $root 'beta'
    New-Item -ItemType Directory -Force -Path $betaFolder | Out-Null

    $version = (Get-Item $exe).VersionInfo.FileVersion
    if (-not $version) { $version = '1.0.0' }

    $zip = Join-Path $betaFolder "SafeChat-beta-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip
    Say "Beta package: beta\SafeChat-beta-$version.zip" 'Green'
    Say 'Whoever gets it needs the .NET 8 Desktop Runtime.' 'DarkGray'
}
catch {
    Log "zip failed: $_"
    Say 'The beta zip could not be made, but the app itself is ready.' 'Yellow'
}
}

if ($SelfContained) {
    Say 'Building a copy that needs nothing installed. This one is large and slow.' 'Yellow'
    Say 'It downloads the .NET runtime files, so this step needs internet access.' 'DarkGray'
    $standalone = Join-Path $root 'build\standalone'

    $arguments = @('publish', $project, '-c', 'Release', '-o', $standalone, '--nologo',
        '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')

    # The runtime files come from the normal package source, so the offline
    # config is stepped aside for this build.
    try {
        if (Test-Path $config) { Move-Item $config $configBackup -Force }
        $text = & $script:Dotnet @arguments 2>&1
        Log ($text | Out-String)
    }
    finally {
        if (Test-Path $configBackup) { Move-Item $configBackup $config -Force }
    }

    if ($LASTEXITCODE -eq 0) {
        $zip = Join-Path $root 'beta\SafeChat-beta-standalone.zip'
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $standalone '*') -DestinationPath $zip
        Say 'Standalone package: beta\SafeChat-beta-standalone.zip' 'Green'
        Say 'That one runs on any Windows 10 or 11 machine with nothing installed.' 'DarkGray'
    }
    else {
        Say 'The standalone build failed. It needs internet access for the runtime files.' 'Yellow'
    }
}

# ================================================================= shortcuts

if (-not $Rebuild) {
try {
    $shell = New-Object -ComObject WScript.Shell

    foreach ($folder in @(
        [Environment]::GetFolderPath('Desktop'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {

        $link = $shell.CreateShortcut((Join-Path $folder 'SafeChat.lnk'))
        $link.TargetPath = $exe
        $link.WorkingDirectory = $output
        $link.IconLocation = "$exe,0"
        $link.Description = 'VPN and time-zone guard for Claude'
        $link.Save()
    }

    Say 'Shortcuts added to the Desktop and Start Menu.' 'Green'
}
catch {
    Log "shortcuts failed: $_"
    Say 'Could not create the shortcuts, but the app is built and ready.' 'Yellow'
}
}

# ==================================================================== finish

Write-Host ''
Say 'Done. Starting SafeChat...' 'Green'
Write-Host ''

Start-Process -FilePath $exe -WorkingDirectory $output
Start-Sleep -Seconds 2

if ($Rebuild -and -not $wasRunning) {
    Say 'It was not running before, so it has been started now.' 'DarkGray'
}
