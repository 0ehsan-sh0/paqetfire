param(
    [string]$XrayPath = "$PSScriptRoot/../artifacts/publish/win-x64/broker/payload/engines/xray/x64/xray.exe",
    [string]$CoreAssemblyPath = "$PSScriptRoot/../src/PaqetFire.Core/bin/Debug/net10.0/PaqetFire.Core.dll"
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CoreAssemblyPath
$share = [PaqetFire.Core.Configuration.LanSocksShare]::new('192.168.50.12', 2082, 'test-user', 'test-password')
$policy = [PaqetFire.Core.Configuration.XrayRoutingPolicy]::new(0, 0, $false, $false, $false, $false, $share)
$config = [PaqetFire.Core.Configuration.XrayJsonConfigurationWriter]::new().Write($policy) | ConvertFrom-Json
# Isolate the generated LAN inbound on loopback; retain its authentication settings.
$reservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$reservation.Start()
$port = $reservation.LocalEndpoint.Port
$reservation.Stop()
$inbound = $config.inbounds | Where-Object tag -eq 'lan-share-in'
$inbound.listen = '127.0.0.1'
$inbound.port = $port
$inbound.settings.ip = '127.0.0.1'
$inbound.settings.udp = $false
$config.inbounds = @($inbound)
$path = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString() + '.json')
$process = $null
try {
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($XrayPath))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('run', '-config', $path)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    foreach ($password in @('test-password', 'wrong-password')) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while (-not $client.Connected) {
                try { $client.Connect('127.0.0.1', $port) }
                catch {
                    if ($process.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Test Xray listener did not start.' }
                    Start-Sleep -Milliseconds 50
                }
            }
            $stream = $client.GetStream()
            $stream.ReadTimeout = 3000
            $stream.Write([byte[]]@(5, 1, 2))
            if ($stream.ReadByte() -ne 5 -or $stream.ReadByte() -ne 2) { throw 'SOCKS5 did not select password authentication.' }
            $userBytes = [Text.Encoding]::UTF8.GetBytes('test-user')
            $passBytes = [Text.Encoding]::UTF8.GetBytes($password)
            $stream.Write([byte[]](@(1, $userBytes.Length) + $userBytes + @($passBytes.Length) + $passBytes))
            $version = $stream.ReadByte()
            $status = $stream.ReadByte()
            if ($password -eq 'test-password') {
                if ($version -ne 1 -or $status -ne 0) { throw 'FAIL: generated LAN listener rejects the configured credentials.' }
                Write-Output 'PASS: configured credentials accepted.'
            } else {
                if ($version -eq 1 -and $status -eq 0) { throw 'FAIL: incorrect password accepted.' }
                Write-Output 'PASS: incorrect password rejected.'
            }
        } finally { $client.Dispose() }
    }
} finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
}
