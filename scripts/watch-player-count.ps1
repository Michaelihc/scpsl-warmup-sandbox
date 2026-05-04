param(
    [string]$HostName = "60.205.222.32",
    [int]$Port = 7777,
    [int]$IntervalSeconds = 10,
    [int]$TimeoutMs = 2000,
    [switch]$ViaAliyun,
    [string]$AliyunRegion = "cn-beijing",
    [string]$AliyunInstanceId = "i-2zedap60ew87d8n8ir39",
    [string]$RemoteHostName = "127.0.0.1",
    [int]$RemotePort = 0,
    [switch]$Once
)

$ErrorActionPreference = "Stop"

function New-A2sInfoPacket {
    param([byte[]]$Challenge)

    $prefix = [byte[]](0xff, 0xff, 0xff, 0xff, 0x54)
    $payload = [Text.Encoding]::ASCII.GetBytes("Source Engine Query`0")

    if ($Challenge -and $Challenge.Length -eq 4) {
        return $prefix + $payload + $Challenge
    }

    return $prefix + $payload
}

function Read-NullTerminatedString {
    param(
        [byte[]]$Data,
        [ref]$Offset
    )

    $start = $Offset.Value
    while ($Offset.Value -lt $Data.Length -and $Data[$Offset.Value] -ne 0) {
        $Offset.Value++
    }

    $length = $Offset.Value - $start
    $value = if ($length -gt 0) {
        [Text.Encoding]::UTF8.GetString($Data, $start, $length)
    } else {
        ""
    }

    if ($Offset.Value -lt $Data.Length) {
        $Offset.Value++
    }

    return $value
}

function Receive-UdpResponse {
    param(
        [System.Net.Sockets.UdpClient]$Client,
        [int]$Timeout
    )

    $async = $Client.BeginReceive($null, $null)
    if (-not $async.AsyncWaitHandle.WaitOne($Timeout)) {
        throw "Timed out after ${Timeout}ms."
    }

    $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    return $Client.EndReceive($async, [ref]$remote)
}

function Get-ServerInfo {
    param(
        [string]$HostName,
        [int]$Port,
        [int]$TimeoutMs
    )

    $addresses = [System.Net.Dns]::GetHostAddresses($HostName)
    $address = $addresses | Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } | Select-Object -First 1
    if (-not $address) {
        throw "Could not resolve an IPv4 address for $HostName."
    }

    $endpoint = New-Object System.Net.IPEndPoint($address, $Port)
    $client = New-Object System.Net.Sockets.UdpClient
    try {
        $client.Client.ReceiveTimeout = $TimeoutMs
        $packet = New-A2sInfoPacket
        [void]$client.Send($packet, $packet.Length, $endpoint)
        $response = Receive-UdpResponse -Client $client -Timeout $TimeoutMs

        if ($response.Length -ge 9 -and $response[4] -eq 0x41) {
            $challenge = [byte[]]($response[5], $response[6], $response[7], $response[8])
            $packet = New-A2sInfoPacket -Challenge $challenge
            [void]$client.Send($packet, $packet.Length, $endpoint)
            $response = Receive-UdpResponse -Client $client -Timeout $TimeoutMs
        }

        if ($response.Length -lt 6 -or $response[4] -ne 0x49) {
            $type = if ($response.Length -gt 4) { "0x{0:x2}" -f $response[4] } else { "none" }
            throw "Unexpected A2S_INFO response type: $type."
        }

        $offset = 6
        $name = Read-NullTerminatedString -Data $response -Offset ([ref]$offset)
        $map = Read-NullTerminatedString -Data $response -Offset ([ref]$offset)
        $folder = Read-NullTerminatedString -Data $response -Offset ([ref]$offset)
        $game = Read-NullTerminatedString -Data $response -Offset ([ref]$offset)

        if ($offset + 6 -gt $response.Length) {
            throw "A2S_INFO response was truncated."
        }

        $appId = [BitConverter]::ToUInt16($response, $offset)
        $offset += 2
        $players = [int]$response[$offset]
        $offset++
        $maxPlayers = [int]$response[$offset]
        $offset++
        $bots = [int]$response[$offset]
        $offset++
        $serverType = [char]$response[$offset]
        $offset++
        $environment = [char]$response[$offset]

        [pscustomobject]@{
            Name = $name
            Map = $map
            Folder = $folder
            Game = $game
            AppId = $appId
            Players = $players
            MaxPlayers = $maxPlayers
            Bots = $bots
            ServerType = $serverType
            Environment = $environment
            Endpoint = "$HostName`:$Port"
        }
    }
    finally {
        $client.Close()
    }
}

function Invoke-AliyunShellScript {
    param(
        [string]$Region,
        [string]$InstanceId,
        [string]$Script,
        [int]$TimeoutSeconds
    )

    $content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Script))
    $run = aliyun ecs RunCommand `
        --RegionId $Region `
        --Type RunShellScript `
        --InstanceId.1 $InstanceId `
        --Timeout $TimeoutSeconds `
        --KeepCommand false `
        --ContentEncoding Base64 `
        --CommandContent $content | ConvertFrom-Json

    $deadline = (Get-Date).AddSeconds([Math]::Max(10, $TimeoutSeconds + 10))
    do {
        Start-Sleep -Seconds 2
        $result = aliyun ecs DescribeInvocationResults --RegionId $Region --InvokeId $run.InvokeId | ConvertFrom-Json
        $invocation = $result.Invocation.InvocationResults.InvocationResult[0]
        if ($invocation.InvocationStatus -in @("Success", "Failed", "Stopped", "Timeout")) {
            if ($invocation.Output) {
                $decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($invocation.Output))
            } else {
                $decoded = ""
            }

            if ($invocation.InvocationStatus -ne "Success" -or $invocation.ExitCode -ne 0) {
                throw "Aliyun command failed with status $($invocation.InvocationStatus), exit $($invocation.ExitCode): $decoded"
            }

            return $decoded
        }
    } while ((Get-Date) -lt $deadline)

    throw "Aliyun command did not finish before the local timeout."
}

function Get-ServerInfoViaAliyun {
    param(
        [string]$Region,
        [string]$InstanceId,
        [string]$RemoteHostName,
        [int]$RemotePort,
        [int]$TimeoutMs
    )

    $remoteTimeout = [Math]::Max(1, [Math]::Ceiling($TimeoutMs / 1000.0))
    $escapedHost = $RemoteHostName.Replace("'", "'\''")
    $script = @"
python3 - <<'PY'
import json
import socket
import struct

host = '$escapedHost'
port = $RemotePort
timeout = $remoteTimeout

def query():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(timeout)
    packet = b'\xff\xff\xff\xffTSource Engine Query\x00'
    try:
        sock.sendto(packet, (host, port))
        data, _ = sock.recvfrom(4096)
        if len(data) >= 9 and data[4] == 0x41:
            sock.sendto(packet + data[5:9], (host, port))
            data, _ = sock.recvfrom(4096)

        if len(data) < 6 or data[4] != 0x49:
            return {
                'ok': False,
                'error': 'Unexpected A2S_INFO response type: ' + (hex(data[4]) if len(data) > 4 else 'none')
            }

        offset = 6
        def read_string():
            nonlocal offset
            end = data.find(b'\x00', offset)
            if end < 0:
                end = len(data)
            value = data[offset:end].decode('utf-8', 'replace')
            offset = end + 1
            return value

        name = read_string()
        game_map = read_string()
        folder = read_string()
        game = read_string()
        if offset + 6 > len(data):
            return {'ok': False, 'error': 'A2S_INFO response was truncated.'}

        app_id = struct.unpack_from('<H', data, offset)[0]
        offset += 2
        players = data[offset]
        max_players = data[offset + 1]
        bots = data[offset + 2]
        server_type = chr(data[offset + 3])
        environment = chr(data[offset + 4])
        return {
            'ok': True,
            'name': name,
            'map': game_map,
            'folder': folder,
            'game': game,
            'appId': app_id,
            'players': players,
            'maxPlayers': max_players,
            'bots': bots,
            'serverType': server_type,
            'environment': environment
        }
    except Exception as exc:
        return {'ok': False, 'error': type(exc).__name__ + ': ' + str(exc)}
    finally:
        sock.close()

print(json.dumps(query(), ensure_ascii=False))
PY
"@

    $output = Invoke-AliyunShellScript -Region $Region -InstanceId $InstanceId -Script $script -TimeoutSeconds ([Math]::Max(20, $remoteTimeout + 10))
    $payload = ($output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1) | ConvertFrom-Json
    if (-not $payload.ok) {
        throw $payload.error
    }

    [pscustomobject]@{
        Name = $payload.name
        Map = $payload.map
        Folder = $payload.folder
        Game = $payload.game
        AppId = [int]$payload.appId
        Players = [int]$payload.players
        MaxPlayers = [int]$payload.maxPlayers
        Bots = [int]$payload.bots
        ServerType = [char]$payload.serverType
        Environment = [char]$payload.environment
        Endpoint = "$RemoteHostName`:$RemotePort via Aliyun"
    }
}

$effectiveRemotePort = if ($RemotePort -gt 0) { $RemotePort } else { $Port }
$watchTarget = if ($ViaAliyun) {
    "${HostName}:$Port with Aliyun fallback to ${RemoteHostName}:$effectiveRemotePort"
} else {
    "${HostName}:$Port"
}

Write-Host "Watching SCP:SL player count at $watchTarget. Press Ctrl+C to stop."

do {
    $timestamp = Get-Date -Format "HH:mm:ss"
    try {
        try {
            $info = Get-ServerInfo -HostName $HostName -Port $Port -TimeoutMs $TimeoutMs
        }
        catch {
            if (-not $ViaAliyun) {
                throw
            }

            $info = Get-ServerInfoViaAliyun `
                -Region $AliyunRegion `
                -InstanceId $AliyunInstanceId `
                -RemoteHostName $RemoteHostName `
                -RemotePort $effectiveRemotePort `
                -TimeoutMs $TimeoutMs
        }

        Write-Host ("[{0}] {1}/{2} players, {3} bots | {4} | {5}" -f `
            $timestamp,
            $info.Players,
            $info.MaxPlayers,
            $info.Bots,
            $info.Name,
            $info.Map)
    }
    catch {
        Write-Host ("[{0}] query failed: {1}" -f $timestamp, $_.Exception.Message) -ForegroundColor Yellow
    }

    if (-not $Once) {
        Start-Sleep -Seconds ([Math]::Max(1, $IntervalSeconds))
    }
} while (-not $Once)
