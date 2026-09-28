using System.IO;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using FlexCompanion.Flex;

namespace FlexCompanion.Station;

/// <summary>
/// Direct 4O3A Power Genius XL TCP telemetry client (port 9008), ported from
/// AetherSDR's GPLv3 PgxlConnection design. Direct link is used for telemetry;
/// OPERATE/STANDBY is sent through the FLEX radio's "amplifier set" command.
/// </summary>
public sealed class PgxlClient : IDisposable
{
    TcpClient? _tcp;
    NetworkStream? _stream;
    CancellationTokenSource? _cts;
    int _seq;
    string _host = "";
    int _port = 9008;
    bool _intentional;
    bool _versionSeen;
    int _generation;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsConnected { get; private set; }
    public bool AutoReconnect { get; set; } = true;
    public string Version { get; private set; } = "";

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<IReadOnlyDictionary<string, string>>? Status;
    public event Action<string>? Alert;

    public async Task ConnectAsync(string host, int port = 9008, CancellationToken ct = default)
    {
        _host = host.Trim();
        _port = port <= 0 ? 9008 : port;
        _intentional = false;
        await ConnectCoreAsync(ct);
    }

    async Task ConnectCoreAsync(CancellationToken outer = default)
    {
        int gen = Interlocked.Increment(ref _generation);   // stops the old read and poll loops
        DisconnectSocketOnly();
        IsConnected = false;
        if (_host.Length == 0) throw new InvalidOperationException("PGXL host is blank.");
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await tcp.ConnectAsync(_host, _port, timeout.Token); }
        catch { tcp.Dispose(); throw; }
        if (gen != _generation) { tcp.Dispose(); return; }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _cts = new CancellationTokenSource();
        _seq = 0;
        _versionSeen = false;
        Version = "";
        var token = _cts.Token;
        _ = Task.Run(() => ReadLoop(token, gen));
    }

    public async Task<uint> SendAsync(string command)
    {
        var s = _stream;
        if (s == null) return 0;
        uint seq = (uint)Interlocked.Increment(ref _seq);
        var bytes = Encoding.ASCII.GetBytes($"C{seq}|{command}\n");
        await _writeLock.WaitAsync();      // poll loop and user commands must not interleave bytes
        try
        {
            await s.WriteAsync(bytes);
            await s.FlushAsync();
        }
        finally { _writeLock.Release(); }
        return seq;
    }

    async Task ReadLoop(CancellationToken ct, int gen)
    {
        string reason = "disconnected";
        try
        {
            using var reader = new StreamReader(_stream!, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                await ProcessLineAsync(line, gen);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; if (gen == _generation) Ui.Post(() => Error?.Invoke(reason)); }

        if (gen != _generation) return;            // superseded by a newer connection
        IsConnected = false;
        Ui.Post(() => Disconnected?.Invoke());
        // Keep retrying every 5 s until it comes back or the user disconnects.
        while (!_intentional && AutoReconnect && gen == _generation)
        {
            try
            {
                await Task.Delay(5000);
                if (_intentional || gen != _generation) return;
                await ConnectCoreAsync();
                return;
            }
            catch (Exception ex)
            {
                Ui.Post(() => Error?.Invoke(ex.Message));
                gen = _generation;                  // the failed attempt bumped it; keep retrying
            }
        }
    }

    async Task ProcessLineAsync(string line, int gen)
    {
        if (!_versionSeen && line.StartsWith('V'))
        {
            Version = line[1..];
            _versionSeen = true;
            IsConnected = true;
            Ui.Post(() => Connected?.Invoke());
            await SendAsync("info");
            await SendAsync("status");
            _ = Task.Run(() => PollLoop(gen));
            return;
        }

        if (line.StartsWith("M|", StringComparison.Ordinal))
        {
            var msg = line.Length > 2 ? line[2..].Trim() : "";
            Ui.Post(() => Alert?.Invoke(msg));
            return;
        }

        string body = "";
        if (line.StartsWith('R'))
        {
            int p1 = line.IndexOf('|');
            int p2 = p1 >= 0 ? line.IndexOf('|', p1 + 1) : -1;
            if (p2 < 0) return;
            var code = line[(p1 + 1)..p2].Trim();
            if (code.Length > 0 && code != "0")
            {
                Ui.Post(() => Error?.Invoke($"PGXL refused command: {code}"));
                return;
            }
            body = line[(p2 + 1)..].Trim();
        }
        else if (line.StartsWith('S'))
        {
            int p = line.IndexOf('|');
            if (p < 0) return;
            body = line[(p + 1)..].Trim();
            int firstEq = body.IndexOf('=');
            if (firstEq > 0)
            {
                int sp = body.LastIndexOf(' ', firstEq);
                if (sp >= 0) body = body[(sp + 1)..];
            }
        }
        else return;

        var kv = ParseKv(body);
        if (kv.Count > 0) Ui.Post(() => Status?.Invoke(kv));
    }

    async Task PollLoop(int gen)
    {
        while (!_intentional && IsConnected && gen == _generation)
        {
            try
            {
                await SendAsync("status");
                await Task.Delay(250);
            }
            catch { break; }
        }
    }

    static Dictionary<string, string> ParseKv(string body)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) d[p[..eq]] = p[(eq + 1)..];
        }
        return d;
    }

    public static double DbmToWatts(string? dbm)
    {
        if (!double.TryParse(dbm, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return double.NaN;
        return Math.Pow(10, d / 10.0) / 1000.0;
    }

    public static double ReturnLossToSwr(string? rl)
    {
        if (!double.TryParse(rl, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return double.NaN;
        var rho = Math.Pow(10, -Math.Abs(d) / 20.0);
        return rho < 0.999 ? (1 + rho) / (1 - rho) : 99.9;
    }

    public void Disconnect()
    {
        _intentional = true;
        Interlocked.Increment(ref _generation); // retire read/poll/reconnect continuations immediately
        DisconnectSocketOnly();
        IsConnected = false;
    }

    void DisconnectSocketOnly()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _cts = null; _stream = null; _tcp = null;
    }

    public void Dispose() => Disconnect();
}
