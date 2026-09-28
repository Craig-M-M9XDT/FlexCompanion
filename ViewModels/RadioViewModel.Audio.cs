using System.Globalization;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

/// <summary>
/// DAX stream helpers. A DAX IQ stream feeds the compact centred FFT; a separate
/// DAX RX audio stream is opened only while Best AGC-T needs post-AGC audio RMS.
/// </summary>
public sealed partial class RadioViewModel
{
    readonly AudioAnalyzer _audio = new();
    readonly IqAnalyzer _iq = new();
    readonly AgcTCalibrator _agc = new();
    volatile uint _daxStreamId, _iqStreamId;
    int _daxChannel, _iqChannel;
    string _iqPanId = "";
    bool _iqPanAssigned;
    bool _audioBusy, _audioRecheck, _iqBusy, _iqRecheck;
    bool _needsDax;
    string _audioStatus = "", _spectrumStatus = "";
    float[]? _spectrum, _specAvg;
    double _spectrumMaxHz = 24000, _spectrumCenterOffsetHz, _spectrumSliceFrac = 0.5;
    int _iqRate = 48000;
    bool _iqLost;
    long _lastIqSpectrumSequence;
    long _lastAetherPanSequence;
    DateTime _fftEnabledAt = DateTime.MinValue;
    bool _aetherTakeoverBusy;
    int _aetherUiPending;
    const int AetherProbeMilliseconds = 1200;
    int _agcSliceIndex = -1;
    string _agcKey = "agc_threshold";
    bool _agcWaitingForAudio;
    bool _agcPreparingSweep;
    int _agcRunEpoch;
    DateTime _agcFiltersChangedAt = DateTime.MinValue;
    readonly Dictionary<string, bool> _agcFilterSnapshot = new(StringComparer.OrdinalIgnoreCase);

    // Noise-mitigation functions that alter the noise statistics used by Best AGC-T.
    // Keep status and set keys separate because SmartSDR+ reports some controls under
    // a different name from the command used to change them.
    static readonly (string StatusKey, string SetKey)[] AgcFilterKeys =
    {
        ("nr", "nr"),
        ("nrf", "nrf"),
        ("nrl", "lms_nr"),
        ("nrs", "speex_nr"),
        ("rnn", "rnnoise"),
        ("nb", "nb"),
        ("anf", "anf"),
        ("anfl", "lms_anf"),
        ("anft", "anft"),
    };

    RelayCommand? _assignDax, _agcAuto, _agcKeep, _agcRestore;

    public RelayCommand AssignDaxCommand => _assignDax ??= new RelayCommand(AssignDax, () => IsConnected && SelectedSlice != null);
    public RelayCommand AgcAutoCommand => _agcAuto ??= new RelayCommand(StartAgcSweep, () => IsConnected && SelectedSlice != null && !_agc.IsRunning && !_agcPreparingSweep);
    public RelayCommand AgcKeepCommand => _agcKeep ??= new RelayCommand(() => { _agc.Keep(); RestoreAgcFilters(); AgcChanged(); }, () => _agc.Recommended >= 0 && !_agc.IsRunning);
    public RelayCommand AgcRestoreCommand => _agcRestore ??= new RelayCommand(() => { _agcRunEpoch++; _agcPreparingSweep = false; _agc.Stop(); _agcWaitingForAudio = false; RestoreAgcFilters(); AgcChanged(); ReleaseAudioIfUnused(); },
                                                                               () => _agc.IsRunning || _agc.Recommended >= 0 || _agcWaitingForAudio || _agcPreparingSweep);

    // ───────────────────────── FFT window ─────────────────────────

    public bool ShowFft
    {
        get => _prefs.ShowFft;
        set
        {
            if (_prefs.ShowFft == value) return;
            _prefs.ShowFft = value;
            if (value) _fftEnabledAt = DateTime.UtcNow;
            OnPropertyChanged();
            ApplyFftPreference();
        }
    }

    public float[]? Spectrum { get => _spectrum; private set => Set(ref _spectrum, value); }
    /// <summary>Half of the displayed span (e.g. 12 kHz span => 6000).</summary>
    public double SpectrumMaxHz { get => _spectrumMaxHz; private set => Set(ref _spectrumMaxHz, value); }
    public double SpectrumSampleRate => _iqRate;
    public bool SpectrumCentered => true;
    /// <summary>Frequency of the display centre relative to the slice carrier, in Hz.</summary>
    public double SpectrumCenterOffsetHz { get => _spectrumCenterOffsetHz; private set => Set(ref _spectrumCenterOffsetHz, value); }
    /// <summary>Where the selected slice sits across the display, 0..1 (NaN when outside it).</summary>
    public double SpectrumSliceFraction { get => _spectrumSliceFrac; private set => Set(ref _spectrumSliceFrac, value); }

    // ── user-selectable FFT width ──
    public static IReadOnlyList<double> FftSpanOptionsKhz { get; } = new[] { 3.0, 6, 12, 24, 48, 96, 192 };

    public double FftSpanKhz
    {
        get => FftSpanOptionsKhz.Contains(_prefs.FftSpanKhz) ? _prefs.FftSpanKhz : 48;
        set
        {
            if (!FftSpanOptionsKhz.Contains(value) || _prefs.FftSpanKhz == value) return;
            _prefs.FftSpanKhz = value;
            OnPropertyChanged();
            _specAvg = null;
            SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
            _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
            _ = ApplyIqRateAsync();
        }
    }

    /// <summary>Displayed span after applying the optional network-saver cap.</summary>
    double EffectiveFftSpanKhz => LowBandwidthMode ? Math.Min(FftSpanKhz, 24.0) : FftSpanKhz;

    readonly record struct SpectrumWindow(double CentreOffsetHz, double SpanHz);

    /// <summary>
    /// Centres the compact FFT on the receive passband, while keeping both the complete
    /// filter and its carrier marker visible. Reported radio filter edges win; mode
    /// defaults cover the short interval before slice metadata arrives.
    /// </summary>
    static SpectrumWindow CalculateSpectrumWindow(string mode, string? filterLo, string? filterHi, double requestedSpanHz)
    {
        (double Lo, double Hi) fallback = mode.ToUpperInvariant() switch
        {
            "USB" or "DIGU" or "FDVU" => (100, 3000),
            "LSB" or "DIGL" or "RTTY" or "FDVL" => (-3000, -100),
            "AM" or "SAM" => (-3000, 3000),
            "FM" => (-8000, 8000),
            "NFM" => (-6000, 6000),
            "CW" or "CWL" or "CWU" or "CWR" => (-500, 500),
            _ => (0, 0),
        };

        double lo = double.TryParse(filterLo, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLo) ? parsedLo : fallback.Lo;
        double hi = double.TryParse(filterHi, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHi) ? parsedHi : fallback.Hi;
        if (hi < lo) (lo, hi) = (hi, lo);

        double centre = (lo + hi) * 0.5;
        double filterWidth = Math.Max(0, hi - lo);
        double halfExtent = Math.Max(Math.Max(Math.Abs(lo - centre), Math.Abs(hi - centre)), Math.Abs(centre));
        double margin = Math.Max(400, filterWidth * 0.12);
        double span = Math.Max(Math.Max(requestedSpanHz, 1000), halfExtent * 2 + margin);
        return new SpectrumWindow(centre, span);
    }

    SpectrumWindow SpectrumWindowFor(double requestedSpanHz)
    {
        var slice = SelectedSlice;
        return CalculateSpectrumWindow(slice?.Mode ?? "",
            slice?.State.GetValueOrDefault("filter_lo"),
            slice?.State.GetValueOrDefault("filter_hi"), requestedSpanHz);
    }

    SpectrumWindow AetherSpectrumWindow => SpectrumWindowFor(FftSpanKhz * 1000.0);

    SpectrumWindow DaxSpectrumWindow
    {
        get
        {
            var window = SpectrumWindowFor(EffectiveFftSpanKhz * 1000.0);
            return LowBandwidthMode && window.SpanHz > 24000 ? window with { SpanHz = 24000 } : window;
        }
    }

    /// <summary>Smallest DAX IQ rate whose bandwidth covers the passband-centred window.</summary>
    int RateForSpan
    {
        get
        {
            if (LowBandwidthMode) return 24000;
            return (DaxSpectrumWindow.SpanHz / 1000.0) switch
            {
                <= 24 => 24000,
                <= 48 => 48000,
                <= 96 => 96000,
                _ => 192000,
            };
        }
    }

    /// <summary>FFT length giving roughly 512 bins across the visible span (1024..8192).</summary>
    int FftSize
    {
        get
        {
            double want = _iqRate / DaxSpectrumWindow.SpanHz * 512;
            int n = 1024;
            while (n < want && n < 8192) n <<= 1;
            return n;
        }
    }

    async Task ApplyIqRateAsync()
    {
        int want = RateForSpan;
        if (want == _iqRate) return;
        var c = _client;
        uint id = _iqStreamId;
        if (c == null || id == 0) { _iqRate = want; OnPropertyChanged(nameof(SpectrumSampleRate)); return; }
        var r = await c.SendAsync($"stream set 0x{id:X8} daxiq_rate={want}");
        if (r.Code != 0) { SpectrumStatus = $"DAX IQ rate {want / 1000} kHz was refused ({FlexClient.ErrorText(r.Code)})."; return; }
        _iqRate = want;
        _iq.Reset();
        _lastIqSpectrumSequence = 0;
        _specAvg = null;
        _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
        OnPropertyChanged(nameof(SpectrumSampleRate));
    }
    public string SpectrumStatus { get => _spectrumStatus; private set => Set(ref _spectrumStatus, value); }
    public string AudioStatus { get => _audioStatus; private set => Set(ref _audioStatus, value); }
    public bool NeedsDax { get => _needsDax; private set => Set(ref _needsDax, value); }

    // DAX RX audio is used only for AGC-T. FFT uses its own DAX IQ stream.
    bool AudioWanted => IsConnected && (_agc.IsRunning || _agcWaitingForAudio || _agcPreparingSweep);
    bool IqWanted => IsConnected && ShowFft;

    void ApplyFftPreference()
    {
        _iqLost = false;            // an explicit toggle may retake a channel another client cleared
        _lastAetherPanSequence = 0;
        if (ShowFft && _fftEnabledAt == DateTime.MinValue) _fftEnabledAt = DateTime.UtcNow;
        SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
        _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
        if (!ShowFft) { Spectrum = null; _specAvg = null; _lastIqSpectrumSequence = 0; }
        _ = EnsureIqStreamAsync();
        _ = EnsureAudioStreamAsync();
    }

    // ── DAX RX audio for Best AGC-T ───────────────────────────────

    int SliceDaxChannel => SelectedSlice is { } s && int.TryParse(s.State.GetValueOrDefault("dax"), out var ch) ? ch : 0;

    async Task EnsureAudioStreamAsync()
    {
        if (_audioBusy) { _audioRecheck = true; return; }
        _audioBusy = true;
        try
        {
            do
            {
                _audioRecheck = false;
                await EnsureAudioStreamCoreAsync();
            } while (_audioRecheck);
        }
        finally { _audioBusy = false; }
    }

    async Task EnsureAudioStreamCoreAsync()
    {
        var c = _client;
        if (!AudioWanted || c == null)
        {
            await RemoveAudioStreamAsync();
            NeedsDax = false;
            AudioStatus = "";
            return;
        }

        int ch = SliceDaxChannel;
        if (ch <= 0)
        {
            await RemoveAudioStreamAsync();
            NeedsDax = true;
            AudioStatus = $"Slice {SelectedSlice?.Letter} has no DAX RX channel, so AGC-T has no audio to analyse.";
            return;
        }
        NeedsDax = false;
        if (_daxStreamId != 0 && _daxChannel == ch) return;

        await RemoveAudioStreamAsync();
        AudioStatus = $"Opening DAX RX {ch} for AGC-T…";
        var (code, body) = await c.SendAsync($"stream create type=dax_rx dax_channel={ch}");
        if (_client != c) return;
        if (code != 0)
        {
            AudioStatus = $"Couldn't open DAX RX {ch} ({FlexClient.ErrorText(code)}).";
            return;
        }
        if (!TryStreamId(body, out var id))
        {
            AudioStatus = $"Radio returned an unexpected DAX RX stream id \"{body}\".";
            return;
        }
        _audio.Reset();
        _daxChannel = ch;
        _daxStreamId = id;
        AudioStatus = $"DAX RX {ch} · AGC-T";
    }

    async Task RemoveAudioStreamAsync()
    {
        uint id = _daxStreamId;
        _daxStreamId = 0;
        _daxChannel = 0;
        _audio.Reset();
        if (id != 0 && _client is { } c)
            await c.SendAsync($"stream remove 0x{id:X8}");
    }

    void ReleaseAudioIfUnused()
    {
        if (!AudioWanted) _ = EnsureAudioStreamAsync();
    }

    void AssignDax()
    {
        if (SelectedSlice is not { } s) return;
        var used = Slices.Where(x => x != s)
                         .Select(x => int.TryParse(x.State.GetValueOrDefault("dax"), out var ch) ? ch : 0)
                         .ToHashSet();
        int free = Enumerable.Range(1, 8).FirstOrDefault(ch => !used.Contains(ch));
        if (free == 0) { LastMessage = "All 8 DAX RX channels are in use by other slices."; return; }
        Send($"slice set {s.Index} dax={free}");
        AudioStatus = $"Assigning DAX RX {free} to slice {s.Letter}…";
    }

    /// <summary>UDP thread.</summary>
    void OnAudioPacket(uint streamId, float[] samples)
    {
        if (streamId != 0 && streamId == _daxStreamId) _audio.Push(samples);
    }

    // ── Aether shared-pan feed for compact spectrum ──────────────

    static bool TryHexStreamId(string? text, out uint id)
    {
        id = 0;
        var s = (text ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    bool TryGetFreshAetherFrame(out AetherPanBridge.PanFrame? frame)
    {
        frame = null;
        if (!ShowFft || SelectedSlice is not { } slice || _radioSerial.Length == 0) return false;
        if (!TryHexStreamId(slice.Pan, out var streamId)) return false;
        if (!AetherPanBridge.Instance.TryGetLatest(_radioSerial, streamId, out frame) || frame == null) return false;
        return (DateTime.UtcNow - frame.ReceivedUtc).TotalMilliseconds < 900;
    }

    /// <summary>
    /// Bridge receive-thread notification. Aether normally emits its pan at ~radio FFT cadence
    /// (often 25 fps). Driving the UI from that source cadence avoids the 25-vs-30 Hz polling
    /// beat that made the trace alternately appear fast and slow. Bursts are coalesced; the UI
    /// always consumes the latest frame for the currently selected pan.
    /// </summary>
    void OnAetherPanFrame(AetherPanBridge.PanFrame frame)
    {
        if (!IsConnected || !ShowFft || _radioSerial.Length == 0
            || !string.Equals(frame.Serial, _radioSerial, StringComparison.OrdinalIgnoreCase)) return;
        if (Interlocked.Exchange(ref _aetherUiPending, 1) != 0) return;
        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _aetherUiPending, 0);
            if (!IsConnected || !ShowFft) return;
            if (TryGetFreshAetherFrame(out var latest) && latest != null)
                ApplyAetherPanFrame(latest);
        });
    }

    void ApplyAetherPanFrame(AetherPanBridge.PanFrame aether)
    {
        if (aether.Sequence == _lastAetherPanSequence) return;
        _lastAetherPanSequence = aether.Sequence;
        if (_iqStreamId != 0 && !_aetherTakeoverBusy) _ = StopDaxIqForAetherAsync();
        SpectrumStatus = "Aether shared pan · source-paced radio FFT · no extra DAX IQ stream";
        _specAvg = null; // radio/Aether pan averaging is already baked into these bins
        Spectrum = ResliceAetherPan(aether);
    }

    float[] ResliceAetherPan(AetherPanBridge.PanFrame frame)
    {
        const int outputBins = 320; // enough for the compact tile; mirrors Aether's lightweight mini-pan idea
        var source = frame.Bins;
        if (source.Length < 2) return source;

        if (SelectedSlice is not { } slice
            || !_pans.TryGetValue(slice.Pan, out var pan)
            || Kv.D(pan.GetValueOrDefault("center")) is not double panCenterMhz
            || Kv.D(pan.GetValueOrDefault("bandwidth")) is not double panBandwidthMhz
            || panBandwidthMhz <= 0
            || Kv.D(slice.State.GetValueOrDefault("RF_frequency")) is not double sliceMhz)
        {
            // Metadata can trail a frame briefly during tune/reconnect. Keep a smooth trace
            // rather than dropping the frame: resample the whole source to the tile width.
            SpectrumMaxHz = FftSpanKhz * 500.0;
            SpectrumCenterOffsetHz = 0;
            SpectrumSliceFraction = double.NaN;
            return ResampleLinear(source, outputBins, 0, source.Length - 1, -160f);
        }

        var window = AetherSpectrumWindow;
        double wantedBandwidthMhz = window.SpanHz / 1e6;
        double panLowMhz = panCenterMhz - panBandwidthMhz * 0.5;
        double wantLowMhz = sliceMhz + window.CentreOffsetHz / 1e6 - wantedBandwidthMhz * 0.5;

        float floor = float.PositiveInfinity;
        for (int i = 0; i < source.Length; i++)
            if (float.IsFinite(source[i])) floor = MathF.Min(floor, source[i]);
        if (!float.IsFinite(floor)) floor = -160f;
        floor = MathF.Max(-180f, floor);

        var output = new float[outputBins];
        int n = source.Length;
        for (int i = 0; i < outputBins; i++)
        {
            double mhz = wantLowMhz + wantedBandwidthMhz * i / (outputBins - 1.0);
            double pos = (mhz - panLowMhz) / panBandwidthMhz * (n - 1.0);
            if (pos < 0 || pos > n - 1)
            {
                output[i] = floor;
                continue;
            }
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, n - 1);
            double f = pos - i0;
            output[i] = (float)(source[i0] * (1.0 - f) + source[i1] * f);
        }

        SpectrumMaxHz = window.SpanHz * 0.5;
        SpectrumCenterOffsetHz = window.CentreOffsetHz;
        double marker = 0.5 - window.CentreOffsetHz / window.SpanHz;
        SpectrumSliceFraction = marker is >= 0 and <= 1 ? marker : double.NaN;
        return output;
    }

    static float[] ResampleLinear(float[] source, int count, double start, double end, float floor)
    {
        if (source.Length == 0 || count <= 0) return Array.Empty<float>();
        var output = new float[count];
        if (source.Length == 1)
        {
            Array.Fill(output, source[0]);
            return output;
        }
        for (int i = 0; i < count; i++)
        {
            double pos = start + (end - start) * i / Math.Max(1, count - 1.0);
            if (pos < 0 || pos > source.Length - 1) { output[i] = floor; continue; }
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, source.Length - 1);
            double f = pos - i0;
            output[i] = (float)(source[i0] * (1.0 - f) + source[i1] * f);
        }
        return output;
    }

    async Task StopDaxIqForAetherAsync()
    {
        if (_aetherTakeoverBusy) return;
        _aetherTakeoverBusy = true;
        try { await RemoveIqStreamAsync(clearSpectrum: false); }
        finally { _aetherTakeoverBusy = false; }
    }

    // ── DAX IQ for compact spectrum ───────────────────────────────

    async Task EnsureIqStreamAsync()
    {
        if (_iqBusy) { _iqRecheck = true; return; }
        _iqBusy = true;
        try
        {
            do
            {
                _iqRecheck = false;
                await EnsureIqStreamCoreAsync();
            } while (_iqRecheck);
        }
        finally { _iqBusy = false; }
    }

    async Task EnsureIqStreamCoreAsync()
    {
        var c = _client;
        var slice = SelectedSlice;
        if (!IqWanted || c == null || slice == null)
        {
            await RemoveIqStreamAsync();
            SpectrumStatus = "";
            return;
        }

        // Prefer Aether's existing radio-generated pan FFT when its localhost bridge is
        // available. This adds zero RF/LAN traffic and avoids a second local FFT entirely.
        if (TryGetFreshAetherFrame(out _))
        {
            await RemoveIqStreamAsync(clearSpectrum: false);
            SpectrumStatus = "Aether shared pan · radio FFT · no extra DAX IQ stream";
            return;
        }

        // Give a just-enabled tile a short chance to see Aether before allocating DAX IQ.
        // If no bridge appears, OnFftTick starts the existing DAX fallback automatically.
        if (_iqStreamId == 0 && _fftEnabledAt != DateTime.MinValue
            && (DateTime.UtcNow - _fftEnabledAt).TotalMilliseconds < AetherProbeMilliseconds)
        {
            SpectrumStatus = "Looking for Aether shared pan…";
            return;
        }

        string pan = slice.Pan;
        if (pan.Length == 0)
        {
            await RemoveIqStreamAsync();
            SpectrumStatus = "Waiting for the selected slice's panadapter…";
            return;
        }
        int panCh = _pans.TryGetValue(pan, out var ps0) && int.TryParse(ps0.GetValueOrDefault("daxiq_channel"), out var pc) ? pc : -1;
        if (_iqStreamId != 0 && string.Equals(_iqPanId, pan, StringComparison.OrdinalIgnoreCase))
        {
            if (panCh < 0 || panCh == _iqChannel) return;          // still ours
            if (panCh == 0)
            {
                // Another client cleared the pan's DAX IQ channel. Don't fight it.
                // Mark it lost BEFORE teardown so RemoveIqStreamAsync preserves _iqPanId;
                // subsequent pan status must not immediately grab the channel back.
                _iqLost = true;
                _iqPanAssigned = false;
                await RemoveIqStreamAsync();
                SpectrumStatus = "Another program removed this pan's DAX IQ channel. Toggle FFT off/on to take it again.";
                return;
            }
            // Another client moved the pan to a different IQ channel: follow it, don't own it.
            _iqPanAssigned = false;
        }
        if (_iqLost && string.Equals(_iqPanId, pan, StringComparison.OrdinalIgnoreCase)) return;
        _iqLost = false;

        await RemoveIqStreamAsync();

        int ch = 0;
        bool assigned = false;
        if (_pans.TryGetValue(pan, out var panState) && int.TryParse(panState.GetValueOrDefault("daxiq_channel"), out var existing) && existing is >= 1 and <= 4)
        {
            ch = existing; // reuse the pan's existing assignment; do not clear it on close
        }
        else
        {
            var used = _pans.Values.Select(p => int.TryParse(p.GetValueOrDefault("daxiq_channel"), out var x) ? x : 0)
                                   .Where(x => x is >= 1 and <= 4).ToHashSet();
            ch = Enumerable.Range(1, 4).FirstOrDefault(x => !used.Contains(x));
            if (ch == 0)
            {
                SpectrumStatus = "All four DAX IQ channels are already assigned.";
                return;
            }
            await TryBindForSelectedSliceAsync();
            var bind = await c.SendAsync($"display pan set {pan} daxiq_channel={ch}");
            if (bind.Code != 0)
            {
                SpectrumStatus = $"Couldn't attach DAX IQ {ch} to this pan ({FlexClient.ErrorText(bind.Code)}).";
                return;
            }
            assigned = true;
        }

        SpectrumStatus = $"Opening DAX IQ {ch}…";
        var created = await c.SendAsync($"stream create type=dax_iq daxiq_channel={ch}");
        if (_client != c) return;
        if (created.Code != 0)
        {
            SpectrumStatus = $"Couldn't open DAX IQ {ch} ({FlexClient.ErrorText(created.Code)}).";
            if (assigned) await c.SendAsync($"display pan set {pan} daxiq_channel=0");
            return;
        }
        if (!TryStreamId(created.Message, out var id))
        {
            SpectrumStatus = $"Radio returned an unexpected DAX IQ stream id \"{created.Message}\".";
            if (assigned) await c.SendAsync($"display pan set {pan} daxiq_channel=0");
            return;
        }

        int wantRate = RateForSpan;
        var rate = await c.SendAsync($"stream set 0x{id:X8} daxiq_rate={wantRate}");
        if (rate.Code != 0)
        {
            await c.SendAsync($"stream remove 0x{id:X8}");
            if (assigned) await c.SendAsync($"display pan set {pan} daxiq_channel=0");
            SpectrumStatus = $"DAX IQ rate was refused ({FlexClient.ErrorText(rate.Code)}).";
            return;
        }

        _iq.Reset();
        _lastIqSpectrumSequence = 0;
        _iq.Configure(FftSize, LowBandwidthMode ? 20 : 30);
        _iqStreamId = id;
        _iqChannel = ch;
        _iqPanId = pan;
        _iqPanAssigned = assigned;
        _iqRate = wantRate;
        OnPropertyChanged(nameof(SpectrumSampleRate));
        SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
        SpectrumStatus = SpectrumStatusText(ch);
    }

    async Task RemoveIqStreamAsync(bool clearSpectrum = true)
    {
        uint id = _iqStreamId;
        int ch = _iqChannel;
        string pan = _iqPanId;
        bool assigned = _iqPanAssigned;
        _iqStreamId = 0; _iqChannel = 0; _iqPanAssigned = false;
        if (!_iqLost) _iqPanId = "";
        _iq.Reset();
        _lastIqSpectrumSequence = 0;
        if (clearSpectrum) { Spectrum = null; _specAvg = null; }
        if (_client is not { } c) return;
        if (id != 0) await c.SendAsync($"stream remove 0x{id:X8}");
        // Only undo a pan assignment Companion created itself. Never tear down a
        // DAX IQ mapping that pre-existed in SmartSDR/Aether or another client.
        if (assigned && ch > 0 && pan.Length > 0)
            await c.SendAsync($"display pan set {pan} daxiq_channel=0");
    }

    static bool TryStreamId(string body, out uint id)
    {
        id = 0;
        var hex = body.Trim().Split('|', ' ')[0];
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    /// <summary>UDP thread.</summary>
    void OnIqPacket(uint streamId, float[] i, float[] q, int count)
    {
        if (streamId != 0 && streamId == _iqStreamId) _iq.Push(i, q, count);
    }

    string SpectrumStatusText(int channel)
    {
        double shownKhz = DaxSpectrumWindow.SpanHz / 1000.0;
        if (!LowBandwidthMode || FftSpanKhz <= 24)
            return $"DAX IQ {channel} · {shownKhz:0.#} kHz span";
        return $"DAX IQ {channel} · Network saver 24 kHz (requested {FftSpanKhz:0} kHz)";
    }

    void OnBandwidthModeChanged()
    {
        SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
        _specAvg = null;
        _lastIqSpectrumSequence = 0;
        _lastAetherPanSequence = 0;
        _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
        _ = ApplyIqRateAsync();
        if (_iqChannel > 0) SpectrumStatus = SpectrumStatusText(_iqChannel);
    }

    /// <summary>Called from the 50 ms meter tick.</summary>
    void OnAudioTick()
    {
        if (_daxStreamId != 0)
        {
            bool live = _audio.IsLive;
            var text = live ? $"DAX RX {_daxChannel} · AGC-T" : $"DAX RX {_daxChannel}: waiting for audio…";
            if (AudioStatus != text && !AudioStatus.StartsWith("Couldn't")) AudioStatus = text;
        }

        if (_agcWaitingForAudio && _audio.IsLive && _audio.RmsDb is var r && !double.IsNaN(r))
        {
            _agcWaitingForAudio = false;
            _ = BeginSweepNowAsync();
        }

    }

    /// <summary>
    /// UI-thread presentation tick. FFT math itself runs in IqAnalyzer's background worker;
    /// this method only consumes the newest completed frame. No backlog is ever queued.
    /// </summary>
    void OnFftTick()
    {
        if (!ShowFft) return;

        // Aether relay path: consume the radio's panadapter FFT that Aether already has.
        // No IQ buffering, no local FFT and no additional radio bandwidth.
        if (TryGetFreshAetherFrame(out var aether) && aether != null)
        {
            // Normal Aether frames arrive through OnAetherPanFrame at the producer's own
            // cadence. This remains as a watchdog for a missed localhost notification.
            ApplyAetherPanFrame(aether);
            return;
        }

        // If Aether wasn't present at enable/connect time, fall back to the original DAX-IQ
        // path after the short probe window. A later Aether frame automatically takes over.
        if (_iqStreamId == 0)
        {
            if (_fftEnabledAt == DateTime.MinValue) _fftEnabledAt = DateTime.UtcNow;
            if ((DateTime.UtcNow - _fftEnabledAt).TotalMilliseconds >= AetherProbeMilliseconds)
                _ = EnsureIqStreamAsync();
            else
                SpectrumStatus = "Looking for Aether shared pan…";
            return;
        }

        if (!_iq.IsLive)
        {
            SpectrumStatus = $"DAX IQ {_iqChannel}: waiting for I/Q…";
            Spectrum = null;
            return;
        }

        SpectrumStatus = SpectrumStatusText(_iqChannel);
        int n = FftSize;
        if (!_iq.TryGetLatest(n, ref _lastIqSpectrumSequence, out var full) || full == null) return;

        var spec = CropToSpan(full);
        var prev = _specAvg;
        if (prev != null && prev.Length == spec.Length)
        {
            for (int i = 0; i < spec.Length; i++)
                spec[i] = spec[i] > prev[i] ? prev[i] + (spec[i] - prev[i]) * 0.65f
                                             : prev[i] + (spec[i] - prev[i]) * 0.22f;
        }
        _specAvg = spec;
        Spectrum = spec;
    }

    /// <summary>
    /// Cuts the passband-centred window out of the full fft-shifted IQ spectrum.
    /// DAX IQ itself is centred on the pan, so this also compensates for slice position.
    /// </summary>
    float[] CropToSpan(float[] full)
    {
        int n = full.Length;
        double binHz = (double)_iqRate / n;
        var window = DaxSpectrumWindow;
        int visible = Math.Clamp((int)Math.Round(window.SpanHz / binHz), 16, n);
        SpectrumMaxHz = visible * binHz * 0.5;

        double sliceOffsetHz = 0;       // slice frequency relative to the pan centre
        bool haveSlice = false;
        if (SelectedSlice is { } s && _pans.TryGetValue(s.Pan, out var pan)
            && Kv.D(s.State.GetValueOrDefault("RF_frequency")) is double sf
            && Kv.D(pan.GetValueOrDefault("center")) is double pcMhz)
        {
            sliceOffsetHz = (sf - pcMhz) * 1e6;
            haveSlice = Math.Abs(sliceOffsetHz) < _iqRate / 2.0;
        }

        double wantedCentreHz = haveSlice ? sliceOffsetHz + window.CentreOffsetHz : 0;
        int centreBin = n / 2 + (int)Math.Round(wantedCentreHz / binHz);
        int start = Math.Clamp(centreBin - visible / 2, 0, n - visible);
        var outp = new float[visible];
        Array.Copy(full, start, outp, 0, visible);

        double viewCentreHz = (start + visible / 2.0 - n / 2.0) * binHz;   // relative to pan centre
        SpectrumCenterOffsetHz = haveSlice ? viewCentreHz - sliceOffsetHz : viewCentreHz;
        if (haveSlice)
        {
            double frac = (sliceOffsetHz - (start - n / 2.0) * binHz) / (visible * binHz);
            SpectrumSliceFraction = frac is >= 0 and <= 1 ? frac : double.NaN;
        }
        else SpectrumSliceFraction = double.NaN;
        return outp;
    }

    // ───────────────────────── AGC-T ─────────────────────────

    string AgcMode => SelectedSlice?.State.GetValueOrDefault("agc_mode") ?? "";
    public bool AgcIsOff => AgcMode.Equals("off", StringComparison.OrdinalIgnoreCase);
    public string AgcModeText => SelectedSlice == null ? "No slice selected"
        : AgcIsOff ? $"Slice {SelectedSlice.Letter}: AGC off — the knob is a fixed gain; the sweep finds a comfortable noise level"
        : $"Slice {SelectedSlice.Letter}: AGC {AgcMode.ToUpperInvariant()} — the sweep finds the knee where noise just starts to drop";

    public int AgcValue
    {
        get
        {
            var key = AgcIsOff ? "agc_off_level" : "agc_threshold";
            return (int)Math.Round(Kv.D(SelectedSlice?.State.GetValueOrDefault(key)) ?? 0);
        }
        set
        {
            if (SelectedSlice is not { } s || _agc.IsRunning) return;
            value = Math.Clamp(value, 0, 100);
            var key = AgcIsOff ? "agc_off_level" : "agc_threshold";
            if (AgcValue == value) return;
            s.State[key] = value.ToString(CultureInfo.InvariantCulture);
            Send($"slice set {s.Index} {key}={value}");
            OnPropertyChanged();
            OnPropertyChanged(nameof(AgcValueText));
        }
    }

    public string AgcValueText => SelectedSlice == null ? "" : AgcValue.ToString(CultureInfo.CurrentCulture);

    public double AgcTargetDb
    {
        get => _prefs.AgcTargetDb;
        set
        {
            value = Math.Clamp(Math.Round(value), -60, -6);
            if (_prefs.AgcTargetDb == value) return;
            _prefs.AgcTargetDb = value;
            _agc.TargetDb = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<AgcTCalibrator.Point> AgcCurve { get; private set; } = Array.Empty<AgcTCalibrator.Point>();
    public int AgcRecommended => _agc.Recommended;
    public bool AgcRunning => _agc.IsRunning || _agcWaitingForAudio || _agcPreparingSweep;
    public int AgcPercent => _agc.Percent;

    public string AgcResultText
    {
        get
        {
            if (_agcWaitingForAudio) return "Waiting for slice audio…";
            if (_agcPreparingSweep) return "Temporarily disabling noise processing and allowing the DSP to settle…";
            if (_agc.IsRunning) return $"Sweeping… {_agc.Percent}%";
            if (_agc.Recommended >= 0)
                return $"Best AGC-T: {_agc.Recommended}  ({(_agc.RecommendedIsKnee ? "knee" : "target level")}). " +
                       $"It's applied now: Keep it, or Restore to go back to {_agc.OriginalValue}.";
            return "Tune to a clear spot with no signals, then press Find best AGC-T.";
        }
    }

    public string AgcWarning
    {
        get
        {
            if (SelectedSlice is not { } s) return "";
            var on = AgcFilterKeys
                .Where(k => IsOnValue(s.State.GetValueOrDefault(k.StatusKey)))
                .Select(k => k.StatusKey.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_agc.IsRunning || _agcWaitingForAudio || _agcPreparingSweep)
                return "Noise reduction / blanker / auto-notch functions are temporarily disabled for the AGC-T scan and will be restored automatically.";
            return on.Count > 0
                ? $"ℹ Best AGC-T will temporarily disable {string.Join(", ", on)} and restore the previous settings when the scan finishes."
                : "";
        }
    }

    static bool IsOnValue(string? value) => value == "1"
        || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    void SuppressAgcFilters(SliceItem s)
    {
        _agcFilterSnapshot.Clear();

        foreach (var (statusKey, setKey) in AgcFilterKeys)
        {
            // Only touch features whose state the radio has actually reported. This avoids
            // probing unavailable/licensed-off DSP functions simply because they exist in UI.
            if (!s.State.TryGetValue(statusKey, out var raw)) continue;
            bool wasOn = IsOnValue(raw);
            _agcFilterSnapshot[setKey] = wasOn;
            if (wasOn) Send($"slice set {s.Index} {setKey}=0");
        }

        _agcFiltersChangedAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(AgcWarning));
    }

    void RestoreAgcFilters(bool noReply = false)
    {
        if (_agcFilterSnapshot.Count == 0 || _agcSliceIndex < 0) return;
        var snapshot = _agcFilterSnapshot.ToArray();
        _agcFilterSnapshot.Clear();

        foreach (var (setKey, wasOn) in snapshot)
        {
            string cmd = $"slice set {_agcSliceIndex} {setKey}={(wasOn ? 1 : 0)}";
            if (noReply && _client is { } c) c.TrySendNoReply(cmd);
            else Send(cmd);
        }
        OnPropertyChanged(nameof(AgcWarning));
    }

    void WireAgc()
    {
        _agc.TargetDb = _prefs.AgcTargetDb;
        _agc.GetValue = () => AgcValue;
        _agc.IsOffMode = () => _agcKey == "agc_off_level";
        _agc.RmsDb = () => _audio.RmsDb;
        _agc.ApplyValue = v =>
        {
            if (_agcSliceIndex < 0) return;
            v = Math.Clamp(v, 0, 100);
            Send($"slice set {_agcSliceIndex} {_agcKey}={v}");
            var s = Slices.FirstOrDefault(x => x.Index == _agcSliceIndex);
            if (s != null)
            {
                s.State[_agcKey] = v.ToString(CultureInfo.InvariantCulture);
                if (s == SelectedSlice) { OnPropertyChanged(nameof(AgcValue)); OnPropertyChanged(nameof(AgcValueText)); }
            }
        };
        _agc.Changed += AgcChanged;
        _agc.Finished += () => { RestoreAgcFilters(); AgcChanged(); ReleaseAudioIfUnused(); };
    }

    bool _agcWired;

    void StartAgcSweep()
    {
        if (!_agcWired) { WireAgc(); _agcWired = true; }
        if (SelectedSlice is not { } s) return;

        _agcRunEpoch++;
        RestoreAgcFilters(); // restore any abandoned previous run while its original slice is still known
        _agcSliceIndex = s.Index;
        _agcKey = AgcIsOff ? "agc_off_level" : "agc_threshold";
        _agc.TargetDb = _prefs.AgcTargetDb;
        _agc.Clear();
        SuppressAgcFilters(s);

        if (_audio.IsLive && _daxChannel == SliceDaxChannel && _daxChannel > 0) _ = BeginSweepNowAsync();
        else
        {
            _agcWaitingForAudio = true;
            AgcChanged();
            _ = EnsureAudioStreamAsync();
        }
    }

    async Task BeginSweepNowAsync()
    {
        if (_agcPreparingSweep || _agc.IsRunning) return;
        _agcPreparingSweep = true;
        int epoch = _agcRunEpoch;
        try
        {
            // Let the radio's DSP settle after NR/NB/ANF functions are switched off,
            // otherwise the first RMS point can still contain the old filter state.
            var elapsed = DateTime.UtcNow - _agcFiltersChangedAt;
            var remain = TimeSpan.FromMilliseconds(400) - elapsed;
            if (remain > TimeSpan.Zero) await Task.Delay(remain);
            if (epoch != _agcRunEpoch || !IsConnected || _agcSliceIndex < 0) return;
            _agc.Start();
            AgcChanged();
        }
        finally { _agcPreparingSweep = false; }
    }

    void AgcChanged()
    {
        AgcCurve = _agc.Curve.ToList();
        OnPropertyChanged(nameof(AgcCurve));
        OnPropertyChanged(nameof(AgcRecommended));
        OnPropertyChanged(nameof(AgcRunning));
        OnPropertyChanged(nameof(AgcPercent));
        OnPropertyChanged(nameof(AgcResultText));
        CommandManagerRefresh();
    }

    // ───────────────────────── hooks from the main file ─────────────────────────

    void OnConnectedExtras()
    {
        if (!_agcWired) { WireAgc(); _agcWired = true; }
        if (ShowFft) _fftEnabledAt = DateTime.UtcNow;
        _lastAetherPanSequence = 0;
        OnPropertyChanged(nameof(TxSpec));
        _ = EnsureIqStreamAsync();
        _ = EnsureAudioStreamAsync();
    }

    void OnCleanupExtras()
    {
        // Orderly user disconnect can happen while a sweep/result is pending. Send the
        // restore as a best-effort write before Cleanup disposes the TCP socket. On a
        // hard link loss this simply returns false because the transport is already gone.
        _agcRunEpoch++;
        _agcPreparingSweep = false;
        if ((_agc.IsRunning || _agc.Recommended >= 0) && _agc.OriginalValue >= 0
            && _agcSliceIndex >= 0 && _client is { } restoreClient)
            restoreClient.TrySendNoReply($"slice set {_agcSliceIndex} {_agcKey}={_agc.OriginalValue}");

        if (_agc.IsRunning) _agc.Stop();
        _agcWaitingForAudio = false;
        RestoreAgcFilters(noReply: true);
        _agc.Clear();
        _daxStreamId = 0;
        _daxChannel = 0;
        // Give back only a pan DAX IQ assignment Companion made itself. A no-reply
        // write is intentional here: Cleanup disposes the connection immediately after
        // this hook returns, so awaiting a normal command would race the socket close.
        if (_iqPanAssigned && _iqPanId.Length > 0 && _client is { } cc)
            cc.TrySendNoReply($"display pan set {_iqPanId} daxiq_channel=0");
        _iqStreamId = 0; _iqChannel = 0; _iqPanId = ""; _iqPanAssigned = false; _iqLost = false;
        _audio.Reset(); _iq.Reset();
        _iq.Configure(0, LowBandwidthMode ? 20 : 30);
        _lastIqSpectrumSequence = 0;
        _lastAetherPanSequence = 0;
        Interlocked.Exchange(ref _aetherUiPending, 0);
        _fftEnabledAt = DateTime.MinValue;
        Spectrum = null; _specAvg = null;
        AudioStatus = ""; SpectrumStatus = ""; NeedsDax = false;
        ResetExtraMeters();
        AgcChanged();
    }

    void OnSliceChangedExtras()
    {
        if (_agc.IsRunning || _agcWaitingForAudio || _agcPreparingSweep)
        {
            _agcRunEpoch++;
            _agcPreparingSweep = false;
            _agcWaitingForAudio = false;
            _agc.Stop();            // mid-sweep: restore the original value on the slice being swept
            RestoreAgcFilters();
            AgcChanged();
        }
        else if (_agc.Recommended >= 0)
        {
            _agc.Keep();            // finished result stays on its slice
            _agc.Clear();
            AgcChanged();
        }
        _specAvg = null;
        _lastIqSpectrumSequence = 0;
        _lastAetherPanSequence = 0;
        if (ShowFft) _fftEnabledAt = DateTime.UtcNow;
        _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
        SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
        RaiseAgcProps();
        _ = EnsureIqStreamAsync();
        _ = EnsureAudioStreamAsync();
    }

    void OnSelectedSliceStatus(IReadOnlyDictionary<string, string> kv)
    {
        if (kv.ContainsKey("dax")) _ = EnsureAudioStreamAsync();
        if (kv.ContainsKey("pan")) _ = EnsureIqStreamAsync();
        if (kv.ContainsKey("mode") || kv.ContainsKey("filter_lo") || kv.ContainsKey("filter_hi"))
        {
            _specAvg = null;
            _lastIqSpectrumSequence = 0;
            _lastAetherPanSequence = 0;
            SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
            _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
            _ = ApplyIqRateAsync();
        }
        if (kv.Keys.Any(k => k.StartsWith("agc", StringComparison.OrdinalIgnoreCase) || k is "nr" or "nrl" or "nrs" or "rnn" or "nrf" or "nb" or "anf" or "anfl" or "anft"))
            RaiseAgcProps();
    }

    void OnSelectedPanStatus(IReadOnlyDictionary<string, string> kv)
    {
        if (kv.ContainsKey("daxiq_channel")) _ = EnsureIqStreamAsync();
    }

    void RaiseAgcProps()
    {
        OnPropertyChanged(nameof(AgcIsOff));
        OnPropertyChanged(nameof(AgcModeText));
        OnPropertyChanged(nameof(AgcValue));
        OnPropertyChanged(nameof(AgcValueText));
        OnPropertyChanged(nameof(AgcWarning));
    }
}
