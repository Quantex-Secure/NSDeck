param(
    [string]$Configuration = "Release",
    [string]$CertificateThumbprint = "",
    [string]$ReleaseBaseUri = "",
    [string[]]$BlockedTerms = @()
)

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "src\NSDeck.Desktop\NSDeck.Desktop.csproj"
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw)
$version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
$publishPath = Join-Path $projectRoot "artifacts\publish-v$version"
$releasePath = Join-Path $projectRoot "release"
$versionedExe = Join-Path $releasePath "NSDeck-$version.exe"
$mainExe = Join-Path $releasePath "NSDeck.exe"
$jeaScriptSource = Join-Path $projectRoot "scripts\Install-NSDeckJea.ps1"
$jeaScriptDestination = Join-Path $releasePath "Install-NSDeckJea.ps1"
$licenseSource = Join-Path $projectRoot "LICENSE"
$projectNoticeSource = Join-Path $projectRoot "NOTICE"
$noticeSource = Join-Path $projectRoot "THIRD-PARTY-NOTICES.md"
$dotnetNoticeSource = Join-Path (Split-Path (Get-Command dotnet).Source) "ThirdPartyNotices.txt"
$licenseDestination = Join-Path $releasePath "LICENSE"
$projectNoticeDestination = Join-Path $releasePath "NOTICE"
$noticeDestination = Join-Path $releasePath "THIRD-PARTY-NOTICES.md"
$dotnetNoticeDestination = Join-Path $releasePath "DOTNET-THIRD-PARTY-NOTICES.txt"

& (Join-Path $projectRoot "scripts\Test-PublicRelease.ps1") -BlockedTerms $BlockedTerms

& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive `
    -ExecutionPolicy Bypass -File (Join-Path $projectRoot "scripts\Test-JeaModule.ps1")
if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell JEA integration tests failed." }

dotnet test (Join-Path $projectRoot "NSDeck.slnx") --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

dotnet publish $projectPath --configuration $Configuration --runtime win-x64 --self-contained true --output $publishPath
if ($LASTEXITCODE -ne 0) { throw "Publishing NSDeck failed." }

New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishPath "NSDeck.exe") -Destination $versionedExe -Force
Copy-Item -LiteralPath $jeaScriptSource -Destination $jeaScriptDestination -Force
Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination -Force
Copy-Item -LiteralPath $projectNoticeSource -Destination $projectNoticeDestination -Force
Copy-Item -LiteralPath $noticeSource -Destination $noticeDestination -Force
if (-not (Test-Path -LiteralPath $dotnetNoticeSource)) { throw "The .NET third-party notices file was not found at $dotnetNoticeSource." }
Copy-Item -LiteralPath $dotnetNoticeSource -Destination $dotnetNoticeDestination -Force

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kitsRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) "Windows Kits\10\bin"
    return Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Sign-Artifact([string]$path) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { return }
    $signTool = Find-SignTool
    if (-not $signTool) { throw "signtool.exe was not found. Install the Windows SDK signing tools." }
    & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $path." }
}

Sign-Artifact $versionedExe

$runningMain = @(Get-CimInstance Win32_Process -Filter "Name = 'NSDeck.exe'" |
    Where-Object { $_.ExecutablePath -eq $mainExe })
if ($runningMain.Count -eq 0) {
    Copy-Item -LiteralPath $versionedExe -Destination $mainExe -Force
}

$zipPath = Join-Path $releasePath "NSDeck-$version-win-x64.zip"
Compress-Archive -LiteralPath $versionedExe, $jeaScriptDestination, $licenseDestination, $projectNoticeDestination, $noticeDestination, $dotnetNoticeDestination -DestinationPath $zipPath -Force
$checksumPath = Join-Path $releasePath "NSDeck-$version-SHA256.txt"
$hash = (Get-FileHash -LiteralPath $versionedExe -Algorithm SHA256).Hash
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath $checksumPath -Value @(
    "$hash  $(Split-Path -Leaf $versionedExe)",
    "$zipHash  $(Split-Path -Leaf $zipPath)"
) -Encoding ascii
$downloadUrl = if ([string]::IsNullOrWhiteSpace($ReleaseBaseUri)) { "" } else { "$($ReleaseBaseUri.TrimEnd('/'))/$(Split-Path -Leaf $versionedExe)" }
$manifest = [ordered]@{
    version = $version
    downloadUrl = $downloadUrl
    sha256 = $hash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releasePath "update-manifest.json") -Encoding utf8

$inno = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($inno) {
    & $inno.Source "/DSourceExe=$versionedExe" (Join-Path $projectRoot "installer\NSDeck.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
    $installer = Get-ChildItem -LiteralPath $releasePath -Filter "NSDeck-Setup-$version.exe" | Select-Object -First 1
    if ($installer) {
        Sign-Artifact $installer.FullName
        $installerHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash
        Add-Content -LiteralPath $checksumPath -Value "$installerHash  $($installer.Name)" -Encoding ascii
    }
}

Write-Host "Release $version created in $releasePath"
if (-not $inno) { Write-Host "Inno Setup was not installed, so the optional installer was not compiled." }
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { Write-Host "Artifacts are unsigned. Supply -CertificateThumbprint after installing a code-signing certificate." }
