namespace FlexCompanion.Flex;

/// <summary>
/// Collects slice audio from a DAX RX stream (fed on the UDP thread) and provides
/// a smoothed RMS level plus an FFT magnitude spectrum on demand (UI thread).
/// </summary>
public sealed class AudioAnalyzer
{
    public const int SampleRate = 24000;
    const int RingSize = 8192;

    readonly object _lock = new();
    readonly float[] _ring = new float[RingSize];
    int _write;
    long _total;
    double _rmsEma;
    bool _haveRms;
    DateTime _lastSample = DateTime.MinValue;

    /// <summary>Smoothing used by the AGC-T calibrator (same as AetherSDR's engine).</summary>
    const double RmsAlpha = 0.30;

    public bool IsLive => (DateTime.UtcNow - _lastSample).TotalMilliseconds < 500;

    /// <summary>Smoothed post-AGC RMS in dBFS (20·log10), or NaN when no audio.</summary>
    public double RmsDb
    {
        get
        {
            lock (_lock)
                return _haveRms && IsLive ? 20 * Math.Log10(Math.Max(_rmsEma, 1e-6)) : double.NaN;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
            _write = 0; _total = 0; _haveRms = false; _rmsEma = 0;
            _lastSample = DateTime.MinValue;
        }
    }

    /// <summary>Called from the UDP thread.</summary>
    public void Push(float[] samples)
    {
        if (samples.Length == 0) return;
        double sum = 0;
        foreach (var v in samples) sum += v * v;
        double rms = Math.Sqrt(sum / samples.Length);
        lock (_lock)
        {
            foreach (var v in samples)
            {
                _ring[_write] = v;
                _write = (_write + 1) & (RingSize - 1);
            }
            _total += samples.Length;
            if (!_haveRms) { _rmsEma = rms; _haveRms = true; }
            else _rmsEma = (1 - RmsAlpha) * _rmsEma + RmsAlpha * rms;
            _lastSample = DateTime.UtcNow;
        }
    }

    float[]? _window;
    float[]? _re, _im;

    /// <summary>
    /// Magnitude spectrum in dBFS of the latest <paramref name="size"/> samples (power of two).
    /// Returns bins 0..size/2 (bin width = 24000/size Hz) or null when no audio is flowing.
    /// </summary>
    public float[]? Spectrum(int size = 2048)
    {
        if (!IsLive) return null;
        if (_window == null || _window.Length != size)
        {
            _window = new float[size];
            for (int i = 0; i < size; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1)));
            _re = new float[size];
            _im = new float[size];
        }
        var re = _re!; var im = _im!;
        lock (_lock)
        {
            if (_total < size) return null;
            int start = (_write - size) & (RingSize - 1);
            for (int i = 0; i < size; i++)
            {
                re[i] = _ring[(start + i) & (RingSize - 1)] * _window[i];
                im[i] = 0;
            }
        }
        Fft(re, im);
        var outDb = new float[size / 2 + 1];
        // Hann window coherent gain 0.5 -> scale so a full-scale sine reads ~0 dBFS.
        double scale = 2.0 / (size * 0.5);
        for (int i = 0; i < outDb.Length; i++)
        {
            double mag = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) * scale;
            outDb[i] = (float)(20 * Math.Log10(Math.Max(mag, 1e-9)));
        }
        return outDb;
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci;
                    float ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
