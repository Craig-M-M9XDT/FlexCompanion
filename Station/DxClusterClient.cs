using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using FlexCompanion.Flex;

namespace FlexCompanion.Station;

public sealed record DxSpot(string Spotter, double FrequencyMhz, string Callsign, string Comment, string Utc, string Source = "Cluster");

/// <summary>
/// Lightweight Telnet DX-cluster client derived from AetherSDR's GPLv3 DxClusterClient design.
/// Supports DX Spider / AR-Cluster / CC Cluster style login prompts and "DX de" spot lines.
/// </summary>
public sealed class DxClusterClient : IDisposable
{
    static readonly Regex SpotRx = new(
        @"^DX\s+de\s+(\S+?):\s+(\d+\.?\d*)\s+(\S+)\s+(.*?)\s+(\d{4})Z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    TcpClient? _tcp;
    NetworkStream? _stream;
    CancellationTokenSource? _cts;
    string _host = "";
    int _port = 7300;
    string _callsign = "";
    bool _intentional;
    int _reconnectAttempt;
    int _generation;                         // bumps on every (re)connect; stale loops exit quietly
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsConnected { get; private set; }
    public bool AutoReconnect { get; set; } = true;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<string>? RawLine;
    public event Action<DxSpot>? Spot;

    public async Task ConnectAsync(string host, int port, string callsign, CancellationToken ct = default)
    {
        _host = host.Trim();
        _port = port <= 0 ? 7300 : port;
        _callsign = callsign.Trim().ToUpperInvariant();
        _intentional = false;
        _reconnectAttempt = 0;
        await ConnectCoreAsync(ct);
    }

    async Task ConnectCoreAsync(CancellationToken outer = default)
    {
        int gen = Interlocked.Increment(ref _generation);   // retire any running loop first
        DisconnectSocketOnly();
        IsConnected = false;
        if (_host.Length == 0) throw new InvalidOperationException("DX cluster host is blank.");
        if (_callsign.Length == 0) throw new InvalidOperationException("Enter your callsign for the DX cluster login.");

        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await tcp.ConnectAsync(_host, _port, timeout.Token); }
        catch { tcp.Dispose(); throw; }
        if (gen != _generation) { tcp.Dispose(); return; }   // superseded while connecting

        _tcp = tcp;
        _stream = tcp.GetStream();
        _cts = new CancellationTokenSource();
        IsConnected = true;
        _reconnectAttempt = 0;
        Ui.Post(() => Connected?.Invoke());
        var stream = _stream;
        var token = _cts.Token;
        _ = Task.Run(() => ReadLoop(stream, token, gen));
    }

    public async Task SendAsync(string command)
    {
        var s = _stream;
        if (!IsConnected || s == null) return;
        var bytes = Encoding.ASCII.GetBytes(command.TrimEnd() + "\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await s.WriteAsync(bytes);
            await s.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Byte-level reader: clusters send the login prompt ("login: ") WITHOUT a line
    /// ending, so a ReadLine-based loop would wait forever. Telnet IAC negotiation
    /// bytes are stripped (and refused) so they don't corrupt lines.
    /// </summary>
    async Task ReadLoop(NetworkStream stream, CancellationToken ct, int gen)
    {
        bool loggedIn = false;
        string reason = "disconnected";
        var buf = new byte[4096];
        var line = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == 0xFF && i + 1 < n)            // Telnet IAC
                    {
                        byte cmd = buf[i + 1];
                        if (cmd is 0xFB or 0xFC or 0xFD or 0xFE && i + 2 < n)
                        {
                            // WILL/WONT/DO/DONT <opt>: answer DONT/WONT so the server stops asking.
                            byte opt = buf[i + 2];
                            byte reply = cmd is 0xFB or 0xFC ? (byte)0xFE : (byte)0xFC;
                            try { await stream.WriteAsync(new byte[] { 0xFF, reply, opt }, ct); } catch { }
                            i += 2;
                        }
                        else i += 1;
                        continue;
                    }
                    if (b == (byte)'\n' || b == (byte)'\r')
                    {
                        if (line.Length > 0) { await HandleLineAsync(line.ToString(), gen); line.Clear(); }
                        continue;
                    }
                    line.Append((char)b);              // Latin-1
                }

                // Unterminated prompt waiting for input?
                if (!loggedIn && line.Length > 0 && IsLoginPrompt(Clean(line.ToString())))
                {
                    var prompt = Clean(line.ToString());
                    line.Clear();
                    if (gen == _generation) Ui.Post(() => RawLine?.Invoke(prompt));
                    await SendAsync(_callsign);
                    loggedIn = true;
                }
                if (line.Length > 4096) line.Clear();   // runaway garbage guard
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; if (gen == _generation) Ui.Post(() => Error?.Invoke(reason)); }

        if (gen != _generation) return;             // replaced by a newer connection
        IsConnected = false;
        Ui.Post(() => Disconnected?.Invoke());
        if (!_intentional && AutoReconnect)
            _ = ScheduleReconnectAsync();

        async Task HandleLineAsync(string raw, int g)
        {
            var l = Clean(raw);
            if (l.Length == 0 || g != _generation) return;
            Ui.Post(() => RawLine?.Invoke(l));
            if (!loggedIn && IsLoginPrompt(l))
            {
                await SendAsync(_callsign);
                loggedIn = true;
                return;
            }
            if (TryParse(l, out var spot))
                Ui.Post(() => Spot?.Invoke(spot));
        }
    }

    async Task ScheduleReconnectAsync()
    {
        int shift = Math.Min(_reconnectAttempt++, 4);
        int delay = Math.Min(5000 * (1 << shift), 60000);
        try
        {
            await Task.Delay(delay);
            if (!_intentional && !IsConnected) await ConnectCoreAsync();
        }
        catch (Exception ex)
        {
            Ui.Post(() => Error?.Invoke(ex.Message));
            if (!_intentional && AutoReconnect) _ = ScheduleReconnectAsync();
        }
    }

    static bool IsLoginPrompt(string line)
    {
        var l = line.ToLowerInvariant().Trim();
        return l.EndsWith("login:") || l.EndsWith("call:") || l.EndsWith("callsign:")
            || l.Contains("enter your call") || l.Contains("enter your callsign") || l.Contains("your call>");
    }

    static bool TryParse(string line, out DxSpot spot)
    {
        spot = default!;
        var m = SpotRx.Match(line);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var khz)) return false;
        var mhz = khz / 1000.0;
        if (mhz <= 0) return false;
        spot = new DxSpot(m.Groups[1].Value, mhz, m.Groups[3].Value,
                          m.Groups[4].Value.Trim(), m.Groups[5].Value + "Z");
        return true;
    }

    static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\t' || (c >= 0x20 && !(c >= 0x7F && c <= 0x9F))) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    public void Disconnect()
    {
        _intentional = true;
        Interlocked.Increment(ref _generation); // retire read/reconnect continuations immediately
        try { if (IsConnected) _ = SendAsync("bye"); } catch { }
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
