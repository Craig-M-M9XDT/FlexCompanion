using System.Buffers;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FlexCompanion.Flex;

/// <summary>
/// Minimal SmartSDR API client: TCP 4992 command/status channel plus a UDP socket that
/// receives FLEX VITA-49 meter/audio/IQ packets. Connects as a non-GUI companion
/// client so it can run alongside an attached GUI station.
/// </summary>
public sealed class FlexClient : IDisposable
{
    readonly ConcurrentDictionary<int, TaskCompletionSource<(uint Code, string Message)>> _pending = new();
    readonly object _writeLock = new();
    readonly TaskCompletionSource<string> _handleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    TcpClient? _tcp;
    NetworkStream? _stream;
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    int _seq;
    long _meterPackets;
    volatile bool _disposed;

    public string Handle { get; private set; } = "";
    public string ProtocolVersion { get; private set; } = "";
    public string Host { get; private set; } = "";
    public int UdpPort { get; private set; }
    public long MeterPackets => Interlocked.Read(ref _meterPackets);

    /// <summary>Latest raw value per meter id, written by the UDP thread.</summary>
    public ConcurrentDictionary<ushort, short> Meters { get; } = new();

    /// <summary>Status body (text after "S&lt;handle&gt;|"). Raised on the UI thread.</summary>
    public event Action<string>? Status;
    /// <summary>Radio "M" messages. Raised on the UI thread.</summary>
    public event Action<string>? Message;
    /// <summary>
    /// DAX / slice audio samples (mono, -1..1) with their VITA stream id.
    /// Raised on the UDP thread - handlers must be quick and thread-safe.
    /// </summary>
    public event Action<uint, float[]>? Audio;
    /// <summary>FLEX DAX IQ samples. Payload is little-endian float32 I/Q pairs.</summary>
    public event Action<uint, float[], float[], int>? Iq;
    /// <summary>Raised on the UDP thread once after a complete VITA meter packet is applied.</summary>
    public event Action? MeterPacket;
    /// <summary>Raised on the UI thread when the TCP link drops.</summary>
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Host = host;
        _tcp = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _tcp.ConnectAsync(host, port, timeout.Token);
        }
        _stream = _tcp.GetStream();

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReadLoop(token));
        _ = Task.Run(() => UdpLoop(token));

        Handle = await _handleTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    public async Task<(uint Code, string Message)> SendAsync(string command, int timeoutMs = 5000)
    {
        var s = _stream;
        if (s == null || _disposed) return (0xFFFFFFFF, "not connected");

        int seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<(uint Code, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        try
        {
            var bytes = Encoding.ASCII.GetBytes($"C{seq}|{command}\n");
            lock (_writeLock) s.Write(bytes, 0, bytes.Length);
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException) { return (0xFFFFFFFF, "no reply"); }
        catch (Exception ex) { return (0xFFFFFFFF, ex.Message); }
        finally { _pending.TryRemove(seq, out _); }
    }

    async Task ReadLoop(CancellationToken ct)
    {
        string reason = "connection closed by radio";
        try
        {
            using var reader = new StreamReader(_stream!, Encoding.ASCII, false, 8192, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (line.Length > 0) HandleLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; }

        foreach (var p in _pending) p.Value.TrySetResult((0xFFFFFFFF, "disconnected"));
        _pending.Clear();
        if (!_disposed) Ui.Post(() => Disconnected?.Invoke(reason));
    }

    void HandleLine(string line)
    {
        switch (line[0])
        {
            case 'V':
                ProtocolVersion = line[1..];
                break;
            case 'H':
                _handleTcs.TrySetResult(line[1..]);
                break;
            case 'R':
            {
                var parts = line[1..].Split('|', 3);
                if (parts.Length >= 2 && int.TryParse(parts[0], out var seq) && _pending.TryRemove(seq, out var tcs))
                {
                    uint code;
                    try { code = parts[1].Length == 0 ? 0 : Convert.ToUInt32(parts[1], 16); }
                    catch { code = 0xFFFFFFFE; }
                    tcs.TrySetResult((code, parts.Length > 2 ? parts[2] : ""));
                }
                break;
            }
            case 'S':
            {
                int bar = line.IndexOf('|');
                if (bar > 0)
                {
                    var body = line[(bar + 1)..];
                    Ui.Post(() => Status?.Invoke(body));
                }
                break;
            }
            case 'M':
            {
                int bar = line.IndexOf('|');
                var msg = bar > 0 ? line[(bar + 1)..] : line[1..];
                Ui.Post(() => Message?.Invoke(msg));
                break;
            }
        }
    }

    async Task UdpLoop(CancellationToken ct)
    {
        var udp = _udp;
        while (!ct.IsCancellationRequested && udp != null)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            ParseVita(r.Buffer);
        }
    }

    /// <summary>
    /// VITA-49 meter packet: 28-byte header (class id present, packet class code 0x8002
    /// in bytes 14-15), then N x { uint16 meter id, int16 raw value }, all big-endian.
    /// </summary>
    void ParseVita(byte[] d)
    {
        if (d.Length < 28) return;
        if ((d[0] & 0x08) == 0) return;                 // no class id -> not a meter packet
        int pcc = (d[14] << 8) | d[15];
        if (pcc == 0x03E3 || pcc == 0x0123) { ParseAudio(d, pcc); return; }
        if (pcc is 0x02E3 or 0x02E4 or 0x02E5 or 0x02E6) { ParseIq(d); return; }
        if (pcc != 0x8002) return;

        bool trailer = (d[0] & 0x04) != 0;
        int sizeBytes = ((d[2] << 8) | d[3]) * 4;
        int end = Math.Min(d.Length, sizeBytes > 0 ? sizeBytes : d.Length) - (trailer ? 4 : 0);
        for (int i = 28; i + 4 <= end; i += 4)
        {
            ushort id = (ushort)((d[i] << 8) | d[i + 1]);
            short v = (short)((d[i + 2] << 8) | d[i + 3]);
            Meters[id] = v;
        }
        Interlocked.Increment(ref _meterPackets);
        try { MeterPacket?.Invoke(); } catch { /* never stop the UDP loop */ }
    }

    /// <summary>
    /// Audio packets: same 28-byte header. 0x03E3 = float32 stereo big-endian (24 kHz),
    /// 0x0123 = reduced-bandwidth int16 mono big-endian.
    /// </summary>
    void ParseAudio(byte[] d, int pcc)
    {
        var handler = Audio;
        if (handler == null) return;
        uint streamId = (uint)((d[4] << 24) | (d[5] << 16) | (d[6] << 8) | d[7]);
        bool trailer = (d[0] & 0x04) != 0;
        int sizeBytes = ((d[2] << 8) | d[3]) * 4;
        int end = Math.Min(d.Length, sizeBytes > 0 ? sizeBytes : d.Length) - (trailer ? 4 : 0);
        int bytes = end - 28;
        if (bytes <= 0) return;

        float[] mono;
        if (pcc == 0x03E3)
        {
            int frames = bytes / 8;
            mono = new float[frames];
            for (int f = 0, i = 28; f < frames; f++, i += 8)
            {
                float l = BitConverter.Int32BitsToSingle((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]);
                float r = BitConverter.Int32BitsToSingle((d[i + 4] << 24) | (d[i + 5] << 16) | (d[i + 6] << 8) | d[i + 7]);
                mono[f] = float.IsFinite(l + r) ? (l + r) * 0.5f : 0f;
            }
        }
        else
        {
            int n = bytes / 2;
            mono = new float[n];
            for (int k = 0, i = 28; k < n; k++, i += 2)
                mono[k] = (short)((d[i] << 8) | d[i + 1]) / 32768f;
        }
        try { handler(streamId, mono); } catch { /* never kill the UDP loop */ }
    }

    /// <summary>
    /// DAX IQ is the exception to normal FLEX network byte order: its I/Q payload
    /// is little-endian float32 pairs. AetherSDR documents the same radio behaviour.
    /// </summary>
    void ParseIq(byte[] d)
    {
        var handler = Iq;
        if (handler == null) return;
        uint streamId = (uint)((d[4] << 24) | (d[5] << 16) | (d[6] << 8) | d[7]);
        bool trailer = (d[0] & 0x04) != 0;
        int sizeBytes = ((d[2] << 8) | d[3]) * 4;
        int end = Math.Min(d.Length, sizeBytes > 0 ? sizeBytes : d.Length) - (trailer ? 4 : 0);
        int bytes = end - 28;
        int pairs = bytes / 8;
        if (pairs <= 0) return;
        var ii = ArrayPool<float>.Shared.Rent(pairs);
        var qq = ArrayPool<float>.Shared.Rent(pairs);
        try
        {
            for (int n = 0, off = 28; n < pairs; n++, off += 8)
            {
                int ib = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24);
                int qb = d[off + 4] | (d[off + 5] << 8) | (d[off + 6] << 16) | (d[off + 7] << 24);
                float iv = BitConverter.Int32BitsToSingle(ib);
                float qv = BitConverter.Int32BitsToSingle(qb);
                ii[n] = float.IsFinite(iv) ? iv : 0;
                qq[n] = float.IsFinite(qv) ? qv : 0;
            }
            try { handler(streamId, ii, qq, pairs); } catch { /* keep UDP loop alive */ }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ii);
            ArrayPool<float>.Shared.Return(qq);
        }
    }

    /// <summary>
    /// Best-effort command write used only during orderly shutdown, when there is no
    /// time to await the radio response before the socket is disposed. The response
    /// (if any) is intentionally unmatched.
    /// </summary>
    public bool TrySendNoReply(string command)
    {
        var s = _stream;
        if (s == null || _disposed || string.IsNullOrWhiteSpace(command)) return false;
        try
        {
            int seq = Interlocked.Increment(ref _seq);
            var bytes = Encoding.ASCII.GetBytes($"C{seq}|{command}\n");
            lock (_writeLock) s.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch { return false; }
    }

    public static string ErrorText(uint code) => code switch
    {
        0x50000003 => "radio rejected this feature / entitlement check",
        0x5000002D => "bad / unsupported field on this radio / firmware",
        0x5000002F => "mode not implemented by this radio",
        0x50000032 => "invalid mode",
        0x50000061 => "DSP algorithm is invalid for the current mode",
        0x50000085 => "command is invalid for the current mode",
        0x50004001 => "command refused by radio in the current context",
        0xE2000000 => "general radio error / operation rejected in the current state",
        0x50000004 => "parameter error",
        0x50000005 => "wrong number or type of parameters",
        0x50000016 => "malformed command",
        0x5000002C => "wrong number of parameters",
        0xFFFFFFFF => "no reply from radio",
        _ => $"error 0x{code:X8}"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        try { _udp?.Close(); } catch { }
        foreach (var p in _pending) p.Value.TrySetResult((0xFFFFFFFF, "disposed"));
    }
}
