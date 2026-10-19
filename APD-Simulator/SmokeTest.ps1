# Offline SmartSDR APD simulator smoke test. NO HARDWARE, TX NEVER ALLOWED.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\dist\apd-sim\APD-Simulator.exe'
if (-not (Test-Path $exe)) { throw "Simulator missing: $exe" }
$proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
$tcp = [System.Net.Sockets.TcpClient]::new()
try {
    $connected = $false
    for ($i = 0; $i -lt 25; $i++) {
        try { $tcp.Connect('127.0.0.1', 4992); $connected = $true; break } catch {
            Start-Sleep -Milliseconds 200
        }
    }
    if (-not $connected) { throw 'APD simulator did not listen on localhost' }
    $stream = $tcp.GetStream()
    $stream.ReadTimeout = 9000
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII)
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.Encoding]::ASCII)
    $writer.AutoFlush = $true

    $hello1 = $reader.ReadLine()
    $hello2 = $reader.ReadLine()
    if (-not $hello1.StartsWith('V') -or -not $hello2.StartsWith('H')) { throw "Bad handshake: $hello1 | $hello2" }

    function Send-TestCommand([int]$seq, [string]$command, [string]$mustContain = '') {
        $writer.WriteLine("C$seq|$command")
        for ($j = 0; $j -lt 60; $j++) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { throw "Disconnected awaiting R$seq" }
            if ($line.StartsWith("R$seq|")) {
                if ($mustContain -and -not $line.Contains($mustContain)) { throw "Unexpected reply: $line" }
                return $line
            }
        }
        throw "No reply for $command"
    }

    Send-TestCommand 1 'client program FlexCompanion' | Out-Null
    Send-TestCommand 2 'info' 'model=FLEX-8600' | Out-Null
    Send-TestCommand 3 'sub slice all' | Out-Null
    Send-TestCommand 4 'sub tx all' | Out-Null
    Send-TestCommand 5 'sub apd all' | Out-Null
    Send-TestCommand 6 'meter list' 'MICPEAK' | Out-Null
    Send-TestCommand 7 'apd enable=1' | Out-Null

    # We must see a fully qualified correction heartbeat after synthetic calibration time.
    $seenActive = $false
    for ($i=0; $i -lt 18; $i++) {
        try {
            $line = $reader.ReadLine()
            if ($line -match 'apd slice=0 ' -and $line -match 'ant=ANT1' -and $line -match 'rfpower=50' -and $line -match 'equalizer_active=1') {
                $seenActive = $true
                break
            }
        } catch [System.IO.IOException] { break }
    }
    if (-not $seenActive) { throw 'No synthetic APD active status observed' }

    $rejected = Send-TestCommand 8 'xmit 1'
    if ($rejected -notmatch '^R8\|5000002D\|') { throw "TX was not explicitly rejected: $rejected" }

    $rejected = Send-TestCommand 9 'file download apd_log 1'
    if ($rejected -notmatch '^R9\|5000002D\|') { throw "Restricted APD download unexpectedly accepted: $rejected" }
    Write-Host 'APD SIMULATOR SMOKE TEST PASSED — loopback handshake, status, calibration, and TX/download refusal'
} finally {
    $tcp.Close()
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    $proc.Dispose()
}
