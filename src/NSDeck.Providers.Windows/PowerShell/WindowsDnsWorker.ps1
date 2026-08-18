Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$sessions = @{}

function Send-NSDeckResponse {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][bool]$Ok,
        [AllowEmptyString()][string]$Payload = '',
        [AllowEmptyString()][string]$ErrorMessage = ''
    )

    $response = [ordered]@{
        id = $Id
        ok = $Ok
        payload = $Payload
        error = $ErrorMessage
    }
    [Console]::Out.WriteLine((Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $response -Depth 4 -Compress))
    [Console]::Out.Flush()
}

function Remove-NSDeckSession {
    param([Parameter(Mandatory = $true)][string]$Key)

    if ($sessions.ContainsKey($Key)) {
        $existing = $sessions[$Key]
        if ($null -ne $existing) {
            Microsoft.PowerShell.Core\Remove-PSSession -Session $existing -ErrorAction SilentlyContinue
        }
        $sessions.Remove($Key)
    }
}

try {
    while ($null -ne ($line = [Console]::In.ReadLine())) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $request = $null
        $requestId = ''
        $sessionKey = ''
        try {
            $request = Microsoft.PowerShell.Utility\ConvertFrom-Json -InputObject $line -ErrorAction Stop
            $requestId = [string]$request.id
            $server = [string]$request.server
            $endpoint = [string]$request.endpointName
            $operation = [string]$request.operation
            if ([string]::IsNullOrWhiteSpace($requestId) -or [string]::IsNullOrWhiteSpace($server) -or
                [string]::IsNullOrWhiteSpace($endpoint) -or [string]::IsNullOrWhiteSpace($operation)) {
                throw 'The Windows DNS worker request was incomplete.'
            }

            $sessionKey = "$server|$endpoint"
            $session = if ($sessions.ContainsKey($sessionKey)) { $sessions[$sessionKey] } else { $null }
            if ($null -eq $session -or $session.State -ne 'Opened') {
                Remove-NSDeckSession -Key $sessionKey
                $session = Microsoft.PowerShell.Core\New-PSSession -ComputerName $server -ConfigurationName $endpoint -ErrorAction Stop
                $sessions[$sessionKey] = $session
            }

            $items = switch ($operation) {
                'ListZones' {
                    @(Microsoft.PowerShell.Core\Invoke-Command -Session $session -ScriptBlock { Get-NSDeckDnsZone } -ErrorAction Stop)
                    break
                }
                'ReadZone' {
                    $zoneName = [string]$request.zoneName
                    if ([string]::IsNullOrWhiteSpace($zoneName)) { throw 'A zone name is required to read Windows DNS records.' }
                    @(Microsoft.PowerShell.Core\Invoke-Command -Session $session -ArgumentList $zoneName -ScriptBlock {
                        param($RequestedZone)
                        Get-NSDeckDnsRecord -ZoneName $RequestedZone
                    } -ErrorAction Stop)
                    break
                }
                'ReplaceZone' {
                    $zoneName = [string]$request.zoneName
                    $recordsJsonBase64 = [string]$request.recordsJsonBase64
                    if ([string]::IsNullOrWhiteSpace($zoneName) -or [string]::IsNullOrWhiteSpace($recordsJsonBase64)) {
                        throw 'A zone name and record payload are required to update Windows DNS records.'
                    }
                    @(Microsoft.PowerShell.Core\Invoke-Command -Session $session -ArgumentList $zoneName, $recordsJsonBase64 -ScriptBlock {
                        param($RequestedZone, $RequestedRecords)
                        Set-NSDeckDnsZoneRecords -ZoneName $RequestedZone -RecordsJsonBase64 $RequestedRecords
                    } -ErrorAction Stop)
                    break
                }
                default { throw "Unsupported Windows DNS worker operation '$operation'." }
            }

            $projection = switch ($operation) {
                'ListZones' { $items | Microsoft.PowerShell.Utility\Select-Object Name; break }
                'ReadZone' { $items | Microsoft.PowerShell.Utility\Select-Object Name, Type, Value, TtlSeconds, Priority; break }
                default { $items | Microsoft.PowerShell.Utility\Select-Object Added, Removed }
            }
            $payload = @($projection) | Microsoft.PowerShell.Utility\ConvertTo-Json -Depth 8 -Compress
            Send-NSDeckResponse -Id $requestId -Ok $true -Payload ([string]$payload)
        }
        catch {
            if (-not [string]::IsNullOrWhiteSpace($sessionKey)) { Remove-NSDeckSession -Key $sessionKey }
            $message = if ($null -ne $_.Exception) { [string]$_.Exception.Message } else { [string]$_ }
            Send-NSDeckResponse -Id $requestId -Ok $false -ErrorMessage $message
        }
    }
}
finally {
    foreach ($key in @($sessions.Keys)) { Remove-NSDeckSession -Key ([string]$key) }
}
