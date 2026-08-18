[CmdletBinding()]
param(
    [string[]]$BlockedTerms = @()
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$thisScript = $MyInvocation.MyCommand.Path
$localBlockedTermsPath = Join-Path $projectRoot '.public-release-blocked-terms.txt'
$excludedDirectories = @('bin', 'obj', 'release', 'artifacts', 'work', '.git', '.vs')
$textExtensions = @(
    '.cs', '.csproj', '.props', '.targets', '.xaml', '.ps1', '.psm1', '.psd1',
    '.md', '.txt', '.json', '.xml', '.yml', '.yaml', '.sln', '.slnx', '.iss', '.gitignore'
)

if (Test-Path -LiteralPath $localBlockedTermsPath) {
    $BlockedTerms = @($BlockedTerms) + @(
        Get-Content -LiteralPath $localBlockedTermsPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('#') }
    )
}

$secretPatterns = [ordered]@{
    'Private key material' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'AWS access key'       = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
    'GitHub token'         = '\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
    'Google API key'       = '\bAIza[0-9A-Za-z_-]{30,}\b'
    'Slack token'          = '\bxox[baprs]-[0-9A-Za-z-]{10,}\b'
    'Stripe live key'      = '\bsk_live_[0-9A-Za-z]{16,}\b'
    'Local user path'      = '(?i)\b[A-Z]:\\Users\\[^\\\s]+\\'
}

$findings = New-Object System.Collections.Generic.List[object]
$files = Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Where-Object {
    $_.FullName -ne $thisScript -and $_.FullName -ne $localBlockedTermsPath -and
    ($textExtensions -contains $_.Extension.ToLowerInvariant() -or $_.Name -in @('.editorconfig', '.gitattributes')) -and
    -not ($_.FullName.Split([IO.Path]::DirectorySeparatorChar) | Where-Object { $excludedDirectories -contains $_ })
}

foreach ($file in $files) {
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        $lineNumber++
        foreach ($pattern in $secretPatterns.GetEnumerator()) {
            if ($line -match $pattern.Value) {
                $findings.Add([pscustomobject]@{ File = $file.FullName; Line = $lineNumber; Reason = $pattern.Key })
            }
        }
        foreach ($term in $BlockedTerms | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
            if ($line.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $findings.Add([pscustomobject]@{ File = $file.FullName; Line = $lineNumber; Reason = "Blocked identity term: $term" })
            }
        }
    }
}

$sensitiveFiles = Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Where-Object {
    $_.FullName -ne $thisScript -and $_.FullName -ne $localBlockedTermsPath -and
    -not ($_.FullName.Split([IO.Path]::DirectorySeparatorChar) | Where-Object { $excludedDirectories -contains $_ }) -and
    ($_.Name -eq 'settings.json' -or $_.Name -match '(?i)(?:credentials|service-account).+\.json$' -or $_.Extension -in @('.pfx', '.p12', '.pem', '.key'))
}
foreach ($file in $sensitiveFiles) {
    $findings.Add([pscustomobject]@{ File = $file.FullName; Line = 0; Reason = 'Sensitive file should not be published' })
}

if ($findings.Count -gt 0) {
    $findings | Sort-Object File, Line, Reason | Format-Table -AutoSize | Out-String | Write-Host
    throw "Public-release scan found $($findings.Count) item(s) that require review."
}

Write-Host "Public-release scan passed across $($files.Count) source and documentation files."
