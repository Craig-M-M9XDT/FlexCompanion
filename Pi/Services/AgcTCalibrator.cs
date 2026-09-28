using Avalonia.Threading;

namespace FlexCompanion.Services;

/// <summary>
/// Finds the best AGC-T for a slice. Ported from AetherSDR's AgcTCalibrator
/// (src/core/AgcTCalibrator.cpp, GPL-3.0, https://github.com/aethersdr/AetherSDR).
///
///  * AGC slow/med/fast: AGC-T is agc_threshold (the AGC knee). Sweep 100 -> 0 and record
///    post-AGC audio RMS; the knee is where the noise "just begins to drop" - found as the
///    point of maximum perpendicular distance from the chord of the RMS-vs-value curve.
///  * AGC off: the knob is agc_off_level (fixed gain). No knee, so solve for the value
///    that puts the audio noise at a comfortable target level.
///
/// The audio must be post-AGC and pre-volume, which is exactly what a DAX RX stream is.
/// </summary>
public sealed class AgcTCalibrator
{
    public readonly record struct Point(int Value, double RmsDb);

    const int SweepStep = 4;

    readonly DispatcherTimer _timer = new();
    readonly List<Point> _curve = new();
    int _sweepValue;

    public AgcTCalibrator() => _timer.Tick += (_, _) => OnStep();

    public Func<int>? GetValue { get; set; }
    public Action<int>? ApplyValue { get; set; }
    public Func<bool>? IsOffMode { get; set; }
    public Func<double>? RmsDb { get; set; }

    public int SettleMs { get; set; } = 280;
    public double TargetDb { get; set; } = -28;

    public bool IsRunning { get; private set; }
    public int OriginalValue { get; private set; } = -1;
    public int Recommended { get; private set; } = -1;
    public bool RecommendedIsKnee { get; private set; }
    public IReadOnlyList<Point> Curve => _curve;
    public int Percent => IsRunning ? (int)(100.0 * (100 - _sweepValue) / 100) : (Recommended >= 0 ? 100 : 0);

    public event Action? Changed;
    public event Action? Finished;

    bool OffMode => IsOffMode?.Invoke() == true;

    public void Start()
    {
        if (IsRunning || GetValue == null || ApplyValue == null) return;
        OriginalValue = GetValue();
        Recommended = -1;
        _curve.Clear();
        IsRunning = true;
        _sweepValue = 100;
        ApplyValue(_sweepValue);
        _timer.Interval = TimeSpan.FromMilliseconds(SettleMs);
        _timer.Start();
        Changed?.Invoke();
    }

    void OnStep()
    {
        if (!IsRunning) { _timer.Stop(); return; }
        Record(_sweepValue);
        if (_sweepValue <= 0)
        {
            FinishSweep();
            return;
        }
        _sweepValue = Math.Max(0, _sweepValue - SweepStep);
        ApplyValue?.Invoke(_sweepValue);
        Changed?.Invoke();
    }

    void FinishSweep()
    {
        _timer.Stop();
        IsRunning = false;
        Recompute();
        if (Recommended >= 0) ApplyValue?.Invoke(Recommended);
        Changed?.Invoke();
        Finished?.Invoke();
    }

    public void Stop()
    {
        _timer.Stop();
        bool had = IsRunning || Recommended >= 0;
        IsRunning = false;
        if (OriginalValue >= 0 && had) ApplyValue?.Invoke(OriginalValue);
        Recommended = -1;
        Changed?.Invoke();
    }

    public void Keep()
    {
        _timer.Stop();
        IsRunning = false;
        if (Recommended >= 0) ApplyValue?.Invoke(Recommended);
        OriginalValue = -1;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (IsRunning) return;
        _curve.Clear();
        Recommended = -1;
        OriginalValue = -1;
        Changed?.Invoke();
    }

    void Record(int value)
    {
        double db = RmsDb?.Invoke() ?? double.NaN;
        if (double.IsNaN(db)) db = -120;
        int i = _curve.FindIndex(p => p.Value == value);
        if (i >= 0) _curve[i] = new Point(value, db);
        else
        {
            _curve.Add(new Point(value, db));
            _curve.Sort((a, b) => a.Value.CompareTo(b.Value));
        }
    }

    void Recompute()
    {
        if (_curve.Count < 3) return;
        var pts = _curve;

        if (OffMode)
        {
            int best = pts[0].Value;
            double bestErr = Math.Abs(pts[0].RmsDb - TargetDb);
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                bool brackets = (a.RmsDb - TargetDb) * (b.RmsDb - TargetDb) <= 0;
                if (brackets && Math.Abs(b.RmsDb - a.RmsDb) > 1e-3)
                {
                    double t = (TargetDb - a.RmsDb) / (b.RmsDb - a.RmsDb);
                    Recommended = Math.Clamp((int)Math.Round(a.Value + t * (b.Value - a.Value)), 0, 100);
                    RecommendedIsKnee = false;
                    return;
                }
                double err = Math.Abs(b.RmsDb - TargetDb);
                if (err < bestErr) { bestErr = err; best = b.Value; }
            }
            Recommended = best;
            RecommendedIsKnee = false;
            return;
        }

        var first = pts[0];
        var last = pts[^1];
        double dx = last.Value - first.Value;
        double dy = last.RmsDb - first.RmsDb;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return;

        int knee = first.Value;
        double maxDist = -1;
        foreach (var p in pts)
        {
            double dist = Math.Abs(dy * (p.Value - first.Value) - dx * (p.RmsDb - first.RmsDb)) / len;
            if (dist > maxDist) { maxDist = dist; knee = p.Value; }
        }
        Recommended = Math.Clamp(knee, 0, 100);
        RecommendedIsKnee = true;
    }
}
