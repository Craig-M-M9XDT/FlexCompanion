using System.Collections.Concurrent;

namespace FlexCompanion.Flex;

/// <summary>
/// Thread-safe DAX-IQ analyzer. Packet ingestion only copies samples into a ring buffer;
/// FFT work is performed by one background worker per radio so the WPF dispatcher never
/// has to execute the transform. The worker is deliberately latest-frame-only: if IQ
/// arrives faster than the display can consume it, stale frames are dropped rather than
/// queued, which keeps tuning and controls responsive in dual-radio operation.
/// </summary>
public sealed class IqAnalyzer : IDisposable
{
    const int Capacity = 16384;

    readonly object _gate = new();
    readonly float[] _i = new float[Capacity];
    readonly float[] _q = new float[Capacity];
    int _write, _count;
    long _sampleVersion;
    long _lastPacketTicks;

    readonly object _resultGate = new();
    float[]? _latestSpectrum;
    int _latestN;
    long _latestSequence;

    readonly CancellationTokenSource _cts = new();
    readonly Task _worker;
    int _requestedN;
    int _targetFps = 30;
    bool _disposed;

    static readonly ConcurrentDictionary<int, FftPlan> Plans = new();

    public IqAnalyzer() => _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));

    public bool IsLive
    {
        get
        {
            long last = Interlocked.Read(ref _lastPacketTicks);
            return last != 0 && (DateTime.UtcNow.Ticks - last) < TimeSpan.FromMilliseconds(800).Ticks;
        }
    }

    /// <summary>Configure background FFT size and display-rate ceiling. Set fftSize to 0 to pause work.</summary>
    public void Configure(int fftSize, int targetFps)
    {
        if (fftSize != 0 && (fftSize < 64 || (fftSize & (fftSize - 1)) != 0))
            throw new ArgumentOutOfRangeException(nameof(fftSize));
        Volatile.Write(ref _requestedN, fftSize);
        Volatile.Write(ref _targetFps, Math.Clamp(targetFps, 5, 60));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _write = 0;
            _count = 0;
            _sampleVersion++;
            Interlocked.Exchange(ref _lastPacketTicks, 0);
        }
        lock (_resultGate)
        {
            _latestSpectrum = null;
            _latestN = 0;
            _latestSequence++;
        }
    }

    /// <summary>
    /// Push sanitized I/Q samples from the UDP thread. FLEX packet parsing already replaces
    /// non-finite floats, so this path can use block copies instead of per-sample validation.
    /// </summary>
    public void Push(float[] i, float[] q, int count = -1)
    {
        if (count < 0) count = Math.Min(i.Length, q.Length);
        count = Math.Min(count, Math.Min(i.Length, q.Length));
        if (count <= 0) return;

        int src = 0;
        if (count > Capacity)
        {
            src = count - Capacity;   // retain newest samples only
            count = Capacity;
        }

        lock (_gate)
        {
            int first = Math.Min(count, Capacity - _write);
            Array.Copy(i, src, _i, _write, first);
            Array.Copy(q, src, _q, _write, first);
            int rest = count - first;
            if (rest > 0)
            {
                Array.Copy(i, src + first, _i, 0, rest);
                Array.Copy(q, src + first, _q, 0, rest);
            }
            _write = (_write + count) % Capacity;
            _count = Math.Min(Capacity, _count + count);
            _sampleVersion++;
            Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
        }
    }

    /// <summary>
    /// Returns the newest completed FFT only once for a caller sequence. The returned array
    /// is immutable after publication, so WPF may safely retain it until the next frame.
    /// </summary>
    public bool TryGetLatest(int n, ref long seenSequence, out float[]? spectrum)
    {
        lock (_resultGate)
        {
            if (_latestSpectrum == null || _latestN != n || _latestSequence == seenSequence)
            {
                spectrum = null;
                return false;
            }
            seenSequence = _latestSequence;
            spectrum = _latestSpectrum;
            return true;
        }
    }

    async Task WorkerLoopAsync(CancellationToken ct)
    {
        long lastProcessedVersion = -1;
        float[] re = Array.Empty<float>();
        float[] im = Array.Empty<float>();
        int scratchN = 0;

        while (!ct.IsCancellationRequested)
        {
            int n = Volatile.Read(ref _requestedN);
            int fps = Volatile.Read(ref _targetFps);
            if (n == 0)
            {
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            try { await Task.Delay(Math.Max(1, 1000 / Math.Max(1, fps)), ct); }
            catch (OperationCanceledException) { break; }

            long version;
            lock (_gate) version = _sampleVersion;
            if (version == lastProcessedVersion) continue;

            var plan = Plans.GetOrAdd(n, static size => new FftPlan(size));
            if (scratchN != n)
            {
                re = new float[n];
                im = new float[n];
                scratchN = n;
            }

            if (!SnapshotBitReversed(plan, re, im)) continue;
            Transform(plan, re, im);

            var db = new float[n];
            float norm = n * 0.5f;               // Hann coherent gain ≈ 0.5
            float normSq = norm * norm;
            for (int k = 0; k < n; k++)
            {
                int src = (k + n / 2) & (n - 1); // fftshift
                float magSq = re[src] * re[src] + im[src] * im[src];
                db[k] = 10f * MathF.Log10(MathF.Max(1e-24f, magSq / normSq));
            }

            lock (_resultGate)
            {
                _latestSpectrum = db;
                _latestN = n;
                _latestSequence++;
            }
            lastProcessedVersion = version;
        }
    }

    bool SnapshotBitReversed(FftPlan plan, float[] re, float[] im)
    {
        int n = plan.N;
        lock (_gate)
        {
            if (_count < n) return false;
            int start = (_write - n + Capacity) % Capacity;
            for (int k = 0; k < n; k++)
            {
                int idx = start + k;
                if (idx >= Capacity) idx -= Capacity;
                int dst = plan.BitReverse[k];
                float w = plan.Window[k];
                re[dst] = _i[idx] * w;
                im[dst] = _q[idx] * w;
            }
            return true;
        }
    }

    static void Transform(FftPlan plan, float[] re, float[] im)
    {
        int n = plan.N;
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int stride = n / len;
            for (int start = 0; start < n; start += len)
            {
                for (int j = 0; j < half; j++)
                {
                    int a = start + j;
                    int b = a + half;
                    int tw = j * stride;
                    float wr = plan.Cos[tw], wi = plan.Sin[tw];
                    float vr = re[b] * wr - im[b] * wi;
                    float vi = re[b] * wi + im[b] * wr;
                    float ur = re[a], ui = im[a];
                    re[a] = ur + vr; im[a] = ui + vi;
                    re[b] = ur - vr; im[b] = ui - vi;
                }
            }
        }
    }

    sealed class FftPlan
    {
        public int N { get; }
        public float[] Window { get; }
        public int[] BitReverse { get; }
        public float[] Cos { get; }
        public float[] Sin { get; }

        public FftPlan(int n)
        {
            N = n;
            Window = new float[n];
            BitReverse = new int[n];
            Cos = new float[n / 2];
            Sin = new float[n / 2];

            int bits = System.Numerics.BitOperations.Log2((uint)n);
            for (int k = 0; k < n; k++)
            {
                Window[k] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * k / Math.Max(1, n - 1));
                BitReverse[k] = ReverseIndex(k, bits);
            }
            for (int k = 0; k < n / 2; k++)
            {
                float a = -2f * MathF.PI * k / n;
                Cos[k] = MathF.Cos(a);
                Sin[k] = MathF.Sin(a);
            }
        }

        static int ReverseIndex(int value, int bits)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r = (r << 1) | (value & 1);
                value >>= 1;
            }
            return r;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _worker.Wait(250); } catch { }
        _cts.Dispose();
    }
}
