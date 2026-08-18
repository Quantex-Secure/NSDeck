param(
    [string]$Configuration = "Release"
)

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "src\NSDeck.Desktop\NSDeck.Desktop.csproj"
$publishPath = Join-Path $projectRoot "release"

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishPath

if ($LASTEXITCODE -ne 0) {
    throw "Publishing NSDeck failed."
}

Write-Host "Published to $publishPath"
