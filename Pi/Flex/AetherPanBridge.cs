using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FlexCompanion.Flex;

/// <summary>
/// Optional localhost bridge for reusing AetherSDR's already-decoded FLEX panadapter
/// FFT frames. A patched AetherSDR instance sends FCSP v1 datagrams to UDP/7331.
/// No extra radio stream, panadapter, DAX IQ channel or FFT is created by Companion.
/// </summary>
public sealed class AetherPanBridge : IDisposable
{
    public const int Port = 7331;
    const int HeaderBytes = 24;
    const int MaxBins = 16384;

    static readonly Lazy<AetherPanBridge> LazyInstance = new(() => new AetherPanBridge());
    public static AetherPanBridge Instance => LazyInstance.Value;

    readonly ConcurrentDictionary<string, ConcurrentDictionary<uint, PanFrame>> _frames =
        new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource _cts = new();
    readonly UdpClient? _udp;
    long _sequence;

    public string? Error { get; }

    /// <summary>Raised on the bridge receive thread when a new decoded pan frame arrives.</summary>
    public event Action<PanFrame>? FrameReceived;

    AetherPanBridge()
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public sealed record PanFrame(string Serial, uint StreamId, float[] Bins, long Sequence, DateTime ReceivedUtc, long SourceTimestampNs);

    public bool TryGetLatest(string serial, uint streamId, out PanFrame? frame)
    {
        frame = null;
        serial = NormalizeSerial(serial);
        if (serial.Length == 0 || streamId == 0) return false;
        return _frames.TryGetValue(serial, out var byStream)
            && byStream.TryGetValue(streamId, out frame);
    }

    static string NormalizeSerial(string? serial) => (serial ?? "").Trim().Trim('"').ToUpperInvariant();

    async Task LoopAsync(CancellationToken ct)
    {
        if (_udp == null) return;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try { Parse(r.Buffer); } catch { /* bad localhost datagram: ignore */ }
        }
    }

    void Parse(byte[] d)
    {
        if (d.Length < HeaderBytes) return;
        if (d[0] != (byte)'F' || d[1] != (byte)'C' || d[2] != (byte)'S' || d[3] != (byte)'P') return;
        if (d[4] != 1) return;

        ushort serialLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6, 2));
        uint streamId = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8, 4));
        ushort binCount = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12, 2));
        long sourceNs = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(16, 8));
        if (serialLen == 0 || binCount < 2 || binCount > MaxBins) return;

        int payload = HeaderBytes + serialLen;
        if (payload < HeaderBytes || payload + binCount * 4 > d.Length) return;

        string serial = NormalizeSerial(Encoding.UTF8.GetString(d, HeaderBytes, serialLen));
        if (serial.Length == 0 || streamId == 0) return;

        var bins = new float[binCount];
        int off = payload;
        for (int i = 0; i < binCount; i++, off += 4)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off, 4));
            float v = BitConverter.Int32BitsToSingle(bits);
            bins[i] = float.IsFinite(v) ? v : -160f;
        }

        var frame = new PanFrame(serial, streamId, bins, Interlocked.Increment(ref _sequence), DateTime.UtcNow, sourceNs);
        var byStream = _frames.GetOrAdd(serial, static _ => new ConcurrentDictionary<uint, PanFrame>());
        byStream[streamId] = frame; // latest frame wins; nothing is queued
        try { FrameReceived?.Invoke(frame); } catch { /* never stop the localhost receive loop */ }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
