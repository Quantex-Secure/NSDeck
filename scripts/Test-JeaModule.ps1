#Requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$installerPath = Join-Path $projectRoot 'scripts\Install-NSDeckJea.ps1'
$workerPath = Join-Path $projectRoot 'src\NSDeck.Providers.Windows\PowerShell\WindowsDnsWorker.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("NSDeck-JeaTest-" + [Guid]::NewGuid().ToString('N'))
$originalModulePath = $env:PSModulePath

try {
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($workerPath, [ref]$null, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        throw "The Windows DNS worker is not valid Windows PowerShell 5.1 syntax: $($parseErrors[0].Message)"
    }

    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $dnsModuleRoot = Join-Path $temporaryRoot 'DnsServer'
    $nsDeckModuleRoot = Join-Path $temporaryRoot 'NSDeck.Jea'
    New-Item -ItemType Directory -Path $dnsModuleRoot, $nsDeckModuleRoot -Force | Out-Null

    $fakeDnsModule = @'
$script:Records = @(
    [pscustomobject]@{
        HostName = 'www'
        RecordType = 'A'
        TimeToLive = [TimeSpan]::FromSeconds(300)
        RecordData = [pscustomobject]@{
            IPv4Address = [pscustomobject]@{ IPAddressToString = '192.0.2.10' }
        }
    }
)
$script:Added = @()
$script:Removed = @()

function Get-DnsServerZone {
    [CmdletBinding()]
    param([string]$Name)
    [pscustomobject]@{ ZoneName = $Name }
}

function Get-DnsServerResourceRecord {
    [CmdletBinding()]
    param([string]$ZoneName)
    $script:Records
}

function Add-DnsServerResourceRecordA {
    [CmdletBinding()]
    param([string]$ZoneName, [string]$Name, [string]$IPv4Address, [TimeSpan]$TimeToLive)
    $script:Added += [pscustomobject]@{ ZoneName = $ZoneName; Name = $Name; Value = $IPv4Address; Ttl = $TimeToLive.TotalSeconds }
}

function Remove-DnsServerResourceRecord {
    [CmdletBinding()]
    param([string]$ZoneName, $InputObject, [switch]$Force)
    $script:Removed += $InputObject
}

function Get-FakeDnsState {
    [pscustomobject]@{ Added = @($script:Added); Removed = @($script:Removed) }
}

Export-ModuleMember -Function Get-DnsServerZone, Get-DnsServerResourceRecord, Add-DnsServerResourceRecordA, Remove-DnsServerResourceRecord, Get-FakeDnsState
'@
    [IO.File]::WriteAllText((Join-Path $dnsModuleRoot 'DnsServer.psm1'), $fakeDnsModule, [Text.UTF8Encoding]::new($false))

    $installer = Get-Content -LiteralPath $installerPath -Raw
    $marker = '$moduleSource'
    $pattern = '(?s)' + [regex]::Escape($marker) + "\s*=\s*@'\r?\n(.*?)\r?\n'@"
    $match = [regex]::Match($installer, $pattern)
    if (-not $match.Success) { throw 'The embedded NSDeck JEA module source was not found.' }
    [IO.File]::WriteAllText((Join-Path $nsDeckModuleRoot 'NSDeck.Jea.psm1'), $match.Groups[1].Value, [Text.UTF8Encoding]::new($false))

    $env:PSModulePath = "$temporaryRoot;$originalModulePath"
    Import-Module DnsServer -Force
    Import-Module (Join-Path $nsDeckModuleRoot 'NSDeck.Jea.psm1') -Force

    $desiredJson = @'
[
  {"name":"www","type":"A","value":"192.0.2.10","ttlSeconds":300,"priority":null},
  {"name":"test","type":"A","value":"192.0.2.25","ttlSeconds":1800,"priority":null}
]
'@
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($desiredJson))
    $result = Set-NSDeckDnsZoneRecords -ZoneName 'example.test' -RecordsJsonBase64 $payload
    $state = Get-FakeDnsState

    if ($result.Added -ne 1 -or $result.Removed -ne 0) {
        throw "Unexpected zone diff: added=$($result.Added), removed=$($result.Removed)."
    }
    if ($state.Added.Count -ne 1 -or $state.Added[0].Name -ne 'test' -or $state.Added[0].Value -ne '192.0.2.25') {
        throw 'The JEA module did not add the expected record.'
    }
    if ($state.Removed.Count -ne 0) {
        throw 'The JEA module unexpectedly removed an existing record.'
    }

    Write-Host 'Windows PowerShell 5.1 JEA zone-write integration test passed.'
}
finally {
    $env:PSModulePath = $originalModulePath
    Remove-Module NSDeck.Jea, DnsServer -Force -ErrorAction SilentlyContinue
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($systemTemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
