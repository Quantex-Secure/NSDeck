#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position = 0)]
    [string]$OperatorGroup,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$EndpointName = 'NSDeck.Dns',

    [string]$TranscriptDirectory = "$env:ProgramData\NSDeck\JEA-Transcripts",

    [switch]$Remove,

    [switch]$KeepLegacyEndpoint
)

$ErrorActionPreference = 'Stop'
$moduleName = 'NSDeck.Jea'
$moduleRoot = Join-Path $env:ProgramFiles "WindowsPowerShell\Modules\$moduleName"
$legacyEndpointName = 'DomainDnsManager.Dns'
$legacyModuleRoot = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules\DomainDnsManager.Jea'

if ($Remove) {
    if (Get-PSSessionConfiguration -Name $EndpointName -ErrorAction SilentlyContinue) {
        Unregister-PSSessionConfiguration -Name $EndpointName -Force
    }
    if (Test-Path -LiteralPath $moduleRoot) {
        Remove-Item -LiteralPath $moduleRoot -Recurse -Force
    }
    if (-not $KeepLegacyEndpoint) {
        if (Get-PSSessionConfiguration -Name $legacyEndpointName -ErrorAction SilentlyContinue) {
            Unregister-PSSessionConfiguration -Name $legacyEndpointName -Force
        }
        if (Test-Path -LiteralPath $legacyModuleRoot) {
            Remove-Item -LiteralPath $legacyModuleRoot -Recurse -Force
        }
    }
    Write-Host "Removed the $EndpointName JEA endpoint and NSDeck JEA module."
    return
}

if ([string]::IsNullOrWhiteSpace($OperatorGroup)) {
    $defaultDomain = [string]$env:USERDOMAIN
    $defaultGroup = if (
        -not [string]::IsNullOrWhiteSpace($defaultDomain) -and
        -not $defaultDomain.Equals([string]$env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase) -and
        -not $defaultDomain.Equals('WORKGROUP', [StringComparison]::OrdinalIgnoreCase)
    ) {
        "$defaultDomain\NSDeck-DnsOperators"
    }
    else {
        ''
    }

    Write-Host 'NSDeck uses a domain security group to control who can change DNS records.'
    $prompt = if ([string]::IsNullOrWhiteSpace($defaultGroup)) {
        'Domain group, for example CONTOSO\NSDeck-DnsOperators'
    }
    else {
        "Domain group [$defaultGroup]"
    }
    $enteredGroup = Read-Host $prompt
    $OperatorGroup = if ([string]::IsNullOrWhiteSpace($enteredGroup)) { $defaultGroup } else { $enteredGroup.Trim() }

    if ([string]::IsNullOrWhiteSpace($OperatorGroup)) {
        throw 'No operator group was supplied. Create a domain security group for the intended DNS operators, then run this setup again.'
    }
}

try {
    $null = ([System.Security.Principal.NTAccount]$OperatorGroup).Translate([System.Security.Principal.SecurityIdentifier])
}
catch {
    throw "Windows could not resolve operator group '$OperatorGroup'. Create the group and add the intended operators before running this setup."
}

Import-Module DnsServer -ErrorAction Stop
if (Test-Path -LiteralPath $moduleRoot) {
    Remove-Item -LiteralPath $moduleRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $moduleRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $moduleRoot 'RoleCapabilities') -Force | Out-Null
New-Item -ItemType Directory -Path $TranscriptDirectory -Force | Out-Null

$moduleSource = @'
Set-StrictMode -Version 2.0

$script:SupportedTypes = @('A', 'AAAA', 'CNAME', 'MX', 'NS', 'PTR', 'SRV', 'TXT')

function ConvertTo-NSDeckRecordModel {
    param(
        [Parameter(Mandatory = $true)]$Record,
        [switch]$IncludeSource
    )

    $type = ([string]$Record.RecordType).ToUpperInvariant()
    if ($script:SupportedTypes -notcontains $type) { return }
    $data = $Record.RecordData
    $value = switch ($type) {
        'A'     { [string]$data.IPv4Address.IPAddressToString }
        'AAAA'  { [string]$data.IPv6Address.IPAddressToString }
        'CNAME' { [string]$data.HostNameAlias }
        'MX'    { [string]$data.MailExchange }
        'NS'    { [string]$data.NameServer }
        'PTR'   { [string]$data.PtrDomainName }
        'SRV'   { '{0} {1} {2} {3}' -f $data.Priority, $data.Weight, $data.Port, $data.DomainName }
        'TXT'   { [string]::Join('', @($data.DescriptiveText)) }
    }
    $priority = if ($type -eq 'MX') { [int]$data.Preference } else { $null }
    $model = [ordered]@{
        Name       = if ([string]::IsNullOrWhiteSpace([string]$Record.HostName)) { '@' } else { [string]$Record.HostName }
        Type       = $type
        Value      = $value
        TtlSeconds = [int][Math]::Round($Record.TimeToLive.TotalSeconds)
        Priority   = $priority
    }
    if ($IncludeSource) { $model.Source = $Record }
    [pscustomobject]$model
}

function Get-NSDeckRecordKey {
    param([Parameter(Mandatory = $true)]$Record)
    $priority = if ($null -eq $Record.Priority) { '' } else { [string]$Record.Priority }
    '{0}|{1}|{2}|{3}|{4}' -f ([string]$Record.Name).Trim().ToLowerInvariant(),
        ([string]$Record.Type).Trim().ToUpperInvariant(), ([string]$Record.Value).Trim(),
        [int]$Record.TtlSeconds, $priority
}

function Get-NSDeckDnsZone {
    [CmdletBinding()]
    param()

    DnsServer\Get-DnsServerZone -ErrorAction Stop |
        Where-Object { -not $_.IsAutoCreated -and -not $_.IsReverseLookupZone -and $_.ZoneName -ne 'TrustAnchors' } |
        ForEach-Object { [pscustomobject]@{ Name = [string]$_.ZoneName } }
}

function Get-NSDeckDnsRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$ZoneName
    )

    $null = DnsServer\Get-DnsServerZone -Name $ZoneName -ErrorAction Stop
    DnsServer\Get-DnsServerResourceRecord -ZoneName $ZoneName -ErrorAction Stop |
        ForEach-Object { ConvertTo-NSDeckRecordModel -Record $_ }
}

function Add-NSDeckDnsRecord {
    param(
        [Parameter(Mandatory = $true)][string]$ZoneName,
        [Parameter(Mandatory = $true)]$Record
    )

    $name = [string]$Record.Name
    $value = [string]$Record.Value
    $ttl = [TimeSpan]::FromSeconds([int]$Record.TtlSeconds)
    switch ([string]$Record.Type) {
        'A' {
            DnsServer\Add-DnsServerResourceRecordA -ZoneName $ZoneName -Name $name -IPv4Address $value -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        'AAAA' {
            DnsServer\Add-DnsServerResourceRecordAAAA -ZoneName $ZoneName -Name $name -IPv6Address $value -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        'CNAME' {
            DnsServer\Add-DnsServerResourceRecordCName -ZoneName $ZoneName -Name $name -HostNameAlias $value -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        'MX' {
            DnsServer\Add-DnsServerResourceRecordMX -ZoneName $ZoneName -Name $name -MailExchange $value -Preference ([uint16]$Record.Priority) -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        'NS' {
            DnsServer\Add-DnsServerResourceRecord -ZoneName $ZoneName -Name $name -NS -NameServer $value -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        'PTR' {
            DnsServer\Add-DnsServerResourceRecordPtr -ZoneName $ZoneName -Name $name -PtrDomainName $value -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        'SRV' {
            $parts = $value.Trim().Split(@(' '), 4, [System.StringSplitOptions]::RemoveEmptyEntries)
            if ($parts.Count -ne 4) { throw "SRV value must be: priority weight port target." }
            DnsServer\Add-DnsServerResourceRecord -ZoneName $ZoneName -Name $name -Srv -Priority ([uint16]$parts[0]) -Weight ([uint16]$parts[1]) -Port ([uint16]$parts[2]) -DomainName $parts[3] -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        'TXT' {
            DnsServer\Add-DnsServerResourceRecord -ZoneName $ZoneName -Name $name -Txt -DescriptiveText $value -TimeToLive $ttl -ErrorAction Stop | Out-Null
        }
        default { throw "Record type '$($Record.Type)' is not enabled in the Windows DNS JEA endpoint." }
    }
}

function Set-NSDeckDnsZoneRecords {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$ZoneName,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$RecordsJsonBase64
    )

    $null = DnsServer\Get-DnsServerZone -Name $ZoneName -ErrorAction Stop
    try {
        $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RecordsJsonBase64))
        $parsed = Microsoft.PowerShell.Utility\ConvertFrom-Json -InputObject $json -ErrorAction Stop
        $desired = @($parsed | ForEach-Object { $_ })
    }
    catch {
        throw "The submitted DNS record payload is invalid. $($_.Exception.Message)"
    }

    foreach ($record in $desired) {
        $record.Type = ([string]$record.Type).Trim().ToUpperInvariant()
        $record.Name = ([string]$record.Name).Trim()
        $record.Value = ([string]$record.Value).Trim()
        if ($script:SupportedTypes -notcontains $record.Type) { throw "Record type '$($record.Type)' is not enabled in this endpoint." }
        if ([string]::IsNullOrWhiteSpace($record.Name) -or [string]::IsNullOrWhiteSpace($record.Value)) { throw 'Record names and values cannot be blank.' }
        if ([int]$record.TtlSeconds -lt 1) { throw 'Record TTL values must be at least one second.' }
        if ($record.Type -eq 'MX' -and $null -eq $record.Priority) { throw 'MX records require a priority.' }
        if ($record.Type -eq 'SRV') {
            $parts = $record.Value.Split(@(' '), 4, [System.StringSplitOptions]::RemoveEmptyEntries)
            if ($parts.Count -ne 4) { throw 'SRV values must be: priority weight port target.' }
            [uint16]$parsed = 0
            if (-not [uint16]::TryParse($parts[0], [ref]$parsed) -or
                -not [uint16]::TryParse($parts[1], [ref]$parsed) -or
                -not [uint16]::TryParse($parts[2], [ref]$parsed)) { throw 'SRV priority, weight, and port must be numbers from 0 through 65535.' }
        }
    }

    $current = @(DnsServer\Get-DnsServerResourceRecord -ZoneName $ZoneName -ErrorAction Stop |
        ForEach-Object { ConvertTo-NSDeckRecordModel -Record $_ -IncludeSource })
    $currentCounts = @{}
    $desiredCounts = @{}
    foreach ($record in $current) {
        $key = Get-NSDeckRecordKey $record
        $currentCounts[$key] = 1 + [int]$currentCounts[$key]
    }
    foreach ($record in $desired) {
        $key = Get-NSDeckRecordKey $record
        $desiredCounts[$key] = 1 + [int]$desiredCounts[$key]
    }

    $toRemove = @()
    $desiredRemaining = @{} + $desiredCounts
    foreach ($record in $current) {
        $key = Get-NSDeckRecordKey $record
        if ([int]$desiredRemaining[$key] -gt 0) { $desiredRemaining[$key] = [int]$desiredRemaining[$key] - 1 }
        else { $toRemove += $record }
    }

    $toAdd = @()
    $currentRemaining = @{} + $currentCounts
    foreach ($record in $desired) {
        $key = Get-NSDeckRecordKey $record
        if ([int]$currentRemaining[$key] -gt 0) { $currentRemaining[$key] = [int]$currentRemaining[$key] - 1 }
        else { $toAdd += $record }
    }

    foreach ($record in $toRemove) {
        DnsServer\Remove-DnsServerResourceRecord -ZoneName $ZoneName -InputObject $record.Source -Force -ErrorAction Stop
    }
    foreach ($record in $toAdd) {
        Add-NSDeckDnsRecord -ZoneName $ZoneName -Record $record
    }

    [pscustomobject]@{ Added = $toAdd.Count; Removed = $toRemove.Count }
}

Export-ModuleMember -Function Get-NSDeckDnsZone, Get-NSDeckDnsRecord, Set-NSDeckDnsZoneRecords
'@

$modulePath = Join-Path $moduleRoot "$moduleName.psm1"
$manifestPath = Join-Path $moduleRoot "$moduleName.psd1"
$rolePath = Join-Path $moduleRoot 'RoleCapabilities\NSDeckDnsOperator.psrc'
[IO.File]::WriteAllText($modulePath, $moduleSource, [Text.UTF8Encoding]::new($false))

New-ModuleManifest -Path $manifestPath -RootModule "$moduleName.psm1" -ModuleVersion '1.0.0' `
    -Guid '4c7f25ea-9e97-4d76-ae3f-a5d0b16e6511' -Author 'Quantex Secure' `
    -CompanyName 'Quantex Secure' -Copyright 'Copyright (C) 2026 Quantex Secure' `
    -Description 'Constrained Windows DNS operations for NSDeck.' `
    -FunctionsToExport @('Get-NSDeckDnsZone', 'Get-NSDeckDnsRecord', 'Set-NSDeckDnsZoneRecords')

New-PSRoleCapabilityFile -Path $rolePath -VisibleFunctions @(
    'Get-NSDeckDnsZone',
    'Get-NSDeckDnsRecord',
    'Set-NSDeckDnsZoneRecords'
)

$moduleParent = [IO.Path]::GetFullPath((Split-Path -Parent $moduleRoot)).TrimEnd('\')
$moduleSearchPaths = @($env:PSModulePath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
    [IO.Path]::GetFullPath($_).TrimEnd('\')
})
if ($moduleSearchPaths -notcontains $moduleParent) {
    throw "The NSDeck JEA module was installed outside this server's PSModulePath: $moduleParent"
}
$null = Test-ModuleManifest -Path $manifestPath -ErrorAction Stop
if (-not (Test-Path -LiteralPath $rolePath -PathType Leaf)) {
    throw "The NSDeck JEA role capability was not created at the expected path: $rolePath"
}

$configurationPath = Join-Path $env:TEMP "$EndpointName.pssc"
$roleDefinitions = @{
    $OperatorGroup = @{ RoleCapabilities = 'NSDeckDnsOperator' }
}
New-PSSessionConfigurationFile -Path $configurationPath -SessionType RestrictedRemoteServer `
    -RunAsVirtualAccount -TranscriptDirectory $TranscriptDirectory `
    -ModulesToImport @($moduleName, 'DnsServer') -RoleDefinitions $roleDefinitions
$null = Test-PSSessionConfigurationFile -Path $configurationPath -ErrorAction Stop

try {
    if (Get-PSSessionConfiguration -Name $EndpointName -ErrorAction SilentlyContinue) {
        Unregister-PSSessionConfiguration -Name $EndpointName -Force
    }
    Register-PSSessionConfiguration -Name $EndpointName -Path $configurationPath -Force
    if (-not $KeepLegacyEndpoint -and $EndpointName -ne $legacyEndpointName) {
        if (Get-PSSessionConfiguration -Name $legacyEndpointName -ErrorAction SilentlyContinue) {
            Unregister-PSSessionConfiguration -Name $legacyEndpointName -Force
        }
        if (Test-Path -LiteralPath $legacyModuleRoot) {
            Remove-Item -LiteralPath $legacyModuleRoot -Recurse -Force
        }
    }
}
finally {
    Remove-Item -LiteralPath $configurationPath -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Installed JEA endpoint: $EndpointName" -ForegroundColor Green
Write-Host "Allowed group: $OperatorGroup"
Write-Host "Role capability: $rolePath"
Write-Host "Transcripts: $TranscriptDirectory"
Write-Host ''
Write-Host "Test from an operator workstation with:"
Write-Host "  Invoke-Command -ComputerName $env:COMPUTERNAME -ConfigurationName '$EndpointName' -ScriptBlock { Get-NSDeckDnsZone }"
