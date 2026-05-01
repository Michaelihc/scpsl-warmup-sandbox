param(
    [string]$HostName = "60.205.222.32",
    [int]$Port = 7777,
    [int]$IntervalSeconds = 10,
    [int]$TimeoutMs = 2000,
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

Write-Host "Watching SCP:SL player count at ${HostName}:$Port. Press Ctrl+C to stop."

do {
    $timestamp = Get-Date -Format "HH:mm:ss"
    try {
        $info = Get-ServerInfo -HostName $HostName -Port $Port -TimeoutMs $TimeoutMs
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
