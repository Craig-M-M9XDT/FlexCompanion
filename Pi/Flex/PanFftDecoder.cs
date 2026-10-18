using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace FlexCompanion.Flex;

/// <summary>
/// Receives native FLEX VITA-49 PCC 0x8003 panadapter data. A complete
/// multi-datagram FFT frame is published atomically, never a partial trace.
/// Only receives data actually addressed to this client's registered UDP port.
/// Does not create panadapters or change existing SmartSDR GUI ownership.
/// </summary>
public sealed class PanFftDecoder
{
    public sealed record Frame(uint StreamId, ushort[] Pixels, uint FrameIndex, DateTime ReceivedUtc);
    sealed class Pending
    {
        public uint Index;
        public ushort[] Pixels = Array.Empty<ushort>();
        public bool[] Covered = Array.Empty<bool>();
        public int CoveredCount;
        public bool Published;
    }

    readonly Dictionary<uint, Pending> _pending = new();
    public ConcurrentDictionary<uint, Frame> Latest { get; } = new();

    public void Push(byte[] datagram)
    {
        const int header = 28, subHeader = 12;
        if (datagram.Length < header + subHeader || (datagram[0] & 0x08) == 0) return;
        int words = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(2, 2));
        int packetEnd = Math.Min(datagram.Length, words * 4);
        if ((datagram[0] & 0x04) != 0) packetEnd -= 4; // VITA trailer
        if (packetEnd < header + subHeader) return;

        uint stream = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(4, 4));
        if (stream == 0) return;
        var sub = datagram.AsSpan(header, subHeader);
        int first = BinaryPrimitives.ReadUInt16BigEndian(sub);
        int count = BinaryPrimitives.ReadUInt16BigEndian(sub[2..]);
        int binSize = BinaryPrimitives.ReadUInt16BigEndian(sub[4..]);
        int total = BinaryPrimitives.ReadUInt16BigEndian(sub[6..]);
        uint index = BinaryPrimitives.ReadUInt32BigEndian(sub[8..]);
        if (total < 2 || total > 16384 || binSize != 2 || count == 0
            || first >= total || count > total - first
            || count * 2 > packetEnd - header - subHeader) return;

        if (!_pending.TryGetValue(stream, out var state) || state.Index != index || state.Pixels.Length != total)
        {
            state = new Pending { Index = index, Pixels = new ushort[total], Covered = new bool[total] };
            _pending[stream] = state;
            // Limit retained partial streams, including old/disconnected pans.
            if (_pending.Count > 16)
                foreach (uint stale in _pending.Keys.Where(x => x != stream).Take(_pending.Count - 16).ToArray())
                    _pending.Remove(stale);
        }
        if (state.Published) return;
        int off = header + subHeader;
        for (int i = 0; i < count; i++)
        {
            int pos = first + i;
            if (!state.Covered[pos]) { state.Covered[pos] = true; state.CoveredCount++; }
            state.Pixels[pos] = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(off + 2 * i, 2));
        }
        if (state.CoveredCount != total) return;
        state.Published = true;
        Latest[stream] = new Frame(stream, state.Pixels, index, DateTime.UtcNow);
    }
}
