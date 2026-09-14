<#
.SYNOPSIS
    Builds a SafeChat installer and puts it on the server as the new release.

.DESCRIPTION
    One command from source to something a customer can download:

        .\tools\release.ps1 -Version 1.1.0 -Notes "Faster startup, Persian manual"

    It publishes the app with the .NET runtime inside it, compiles the
    installer with Inno Setup, checks it came out whole, and uploads it. Every
    copy already installed sees it within a few hours and offers it to whoever
    is sitting there.

    Nothing here touches the customers' machines. It publishes; they choose.

.PARAMETER Version
    The number this build gets, like 1.1.0. Written into the project so the
    app reports it, and used to name the installer. Left out, the number
    already in the project is used as it is.

.PARAMETER Notes
    One line the customer reads before accepting the update.

.PARAMETER Server
    The orders server. Defaults to the live one.

.PARAMETER Password
    The admin password. Left out, it is asked for without being shown.

.PARAMETER NoUpload
    Build the installer and stop. Use it to test a build before anyone gets it.

.PARAMETER Draft
    Upload it but leave it switched off, so it is on the server and nobody is
    offered it until you turn it on in the panel.

.PARAMETER Source
    Where to get the .NET runtime files that go inside the installer. The
    project itself has no packages and nuget.config clears every source so an
    ordinary build needs no connection; this one step does, because a
    self-contained app carries the runtime and the runtime comes from there.
    Downloaded once, then cached, so only the first build needs it.
#>

[CmdletBinding()]
param(
    [string] $Version,
    [string] $Notes = '',
    [string] $Server = 'https://app.safechat.ir',
    [string] $Password,
    [switch] $NoUpload,
    [switch] $Draft,
    [string] $Source = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 still offers TLS 1.0 first, which the server refuses.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root      = Split-Path -Parent $PSScriptRoot
$appProj   = Join-Path $root 'src\ClaudeWatch.App\ClaudeWatch.App.csproj'
$issFile   = Join-Path $root 'tools\SafeChat.iss'
$publishTo = Join-Path $root 'build\publish'
$outputTo  = Join-Path $root 'build'

function Say([string] $text, [string] $colour = 'Cyan') {
    Write-Host ''
    Write-Host "== $text" -ForegroundColor $colour
}

function Fail([string] $text) {
    Write-Host ''
    Write-Host "   $text" -ForegroundColor Red
    Write-Host ''
    exit 1
}

# ------------------------------------------------------------------ version

if (-not (Test-Path $appProj)) { Fail "Cannot find the app project at $appProj" }

$projectText = Get-Content $appProj -Raw

if ($Version) {
    if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
        Fail "A version looks like 1.2.3, not '$Version'."
    }

    $projectText = [regex]::Replace(
        $projectText, '<Version>[^<]*</Version>', "<Version>$Version</Version>")

    # Written without a byte-order mark, so the project file does not change
    # shape in git every time a version is cut.
    [System.IO.File]::WriteAllText(
        $appProj, $projectText, (New-Object System.Text.UTF8Encoding $false))

    Write-Host "   Project version set to $Version"
}
else {
    if ($projectText -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1].Trim()
    }
    else {
        Fail 'The project has no <Version>, so pass -Version.'
    }
}

Say "SafeChat $Version"

# ------------------------------------------------------------------ publish

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'The .NET SDK is not installed. Run:  winget install Microsoft.DotNet.SDK.8'
}

Say 'Publishing, with the .NET runtime inside'

if (Test-Path $publishTo) { Remove-Item $publishTo -Recurse -Force }

# Self-contained on purpose. A customer on a bare Windows, behind a filtered
# connection, cannot be sent to Microsoft to fetch a runtime first.
#
# nuget.config clears every source so an ordinary build of this project never
# touches the network. This is the one step that does: the runtime travelling
# inside the installer is downloaded rather than part of the SDK. It is fetched
# once and cached in %USERPROFILE%\.nuget\packages, and --source overrides the
# cleared list for this command alone.
Write-Host "   runtime from $Source"

$log = Join-Path ([System.IO.Path]::GetTempPath()) 'safechat-publish.log'

& dotnet publish $appProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --source $Source `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en `
    -o $publishTo `
    --nologo -v quiet 2>&1 | Tee-Object -FilePath $log

if ($LASTEXITCODE -ne 0) {
    if (Select-String -Path $log -Pattern 'NU1301|NU1100|service index' -Quiet) {
        Fail @"
Could not reach $Source to fetch the .NET runtime files.

   This one step needs a connection. Turn the VPN on and run it again. Once it
   works the files are cached and every later build is offline again.

   To pull them from somewhere else instead:
       .\tools\release.ps1 -Version $Version -Source https://your-mirror/v3/index.json
"@
    }

    Fail 'The build failed. Nothing was uploaded.'
}

$exe = Join-Path $publishTo 'SafeChat.exe'
if (-not (Test-Path $exe)) { Fail 'The build produced no SafeChat.exe.' }

$published = [math]::Round((Get-ChildItem $publishTo -Recurse -File |
    Measure-Object Length -Sum).Sum / 1MB, 0)
Write-Host "   $published MB of files"

# ---------------------------------------------------------------- installer

Say 'Compiling the installer'

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host '   Inno Setup is not here. Installing it...' -ForegroundColor Yellow

    if (Get-Command winget -ErrorAction SilentlyContinue) {
        & winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    }

    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $iscc) {
    Fail 'Inno Setup could not be installed. Get it from https://jrsoftware.org/isdl.php and run this again.'
}

& $iscc "/DAppVersion=$Version" "/DSourceDir=$publishTo" "/DOutputDir=$outputTo" $issFile

if ($LASTEXITCODE -ne 0) { Fail 'The installer did not compile. Nothing was uploaded.' }

$setup = Join-Path $outputTo "SafeChat-Setup-$Version.exe"
if (-not (Test-Path $setup)) { Fail "Expected $setup and it is not there." }

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()

Write-Host ''
Write-Host "   $setup" -ForegroundColor Green
Write-Host "   $size MB"
Write-Host "   sha256 $hash"

if ($NoUpload) {
    Say 'Built, not uploaded (-NoUpload).' 'Yellow'
    Write-Host '   Run it yourself to check it, then run this again without -NoUpload.'
    Write-Host ''
    exit 0
}

# ------------------------------------------------------------------- upload

Say "Uploading to $Server"

if (-not $Password) {
    $secure = Read-Host 'Admin password' -AsSecureString
    $Password = [System.Net.NetworkCredential]::new('', $secure).Password
}

try {
    $null = Invoke-RestMethod -Uri "$Server/api/admin/login" -Method Post `
        -ContentType 'application/json' `
        -Body (@{ password = $Password } | ConvertTo-Json) `
        -SessionVariable session -TimeoutSec 30
}
catch {
    Fail "The server would not accept that password. The installer is still at $setup"
}

$query = "?notes=$([uri]::EscapeDataString($Notes))"

try {
    $answer = Invoke-RestMethod -Uri "$Server/api/admin/releases/$Version$query" -Method Put `
        -InFile $setup -ContentType 'application/octet-stream' `
        -Headers @{ 'X-CW' = '1' } -WebSession $session -TimeoutSec 1800
}
catch {
    Fail "The upload failed: $($_.Exception.Message)`n   The installer is still at $setup"
}

if ($answer.sha256 -ne $hash) {
    Fail "The server received something other than what was sent. Nothing is live; upload it again."
}

if ($Draft) {
    $null = Invoke-RestMethod -Uri "$Server/api/admin/releases/$Version/live?on=false" -Method Post `
        -Headers @{ 'X-CW' = '1' } -WebSession $session -TimeoutSec 30
}

# ------------------------------------------------------------------- result

Write-Host ''
Write-Host "   Version:  $Version" -ForegroundColor Green
Write-Host "   Download: $($answer.url)"
Write-Host "   Size:     $size MB"

if ($Draft) {
    Write-Host '   Status:   uploaded but switched off. Turn it on in the panel when you are ready.' -ForegroundColor Yellow
}
else {
    Write-Host '   Status:   live. Every installed copy offers it within a few hours.' -ForegroundColor Green
}

Write-Host ''
