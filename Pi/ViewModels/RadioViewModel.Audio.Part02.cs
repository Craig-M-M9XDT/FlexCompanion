using System.Globalization;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

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

    bool AudioWanted => IsConnected && (_agc.IsRunning || _agcWaitingForAudio || _agcPreparingSweep);
    bool IqWanted => IsConnected && ShowFft;

    void ApplyFftPreference()
    {
        _iqLost = false;
        _lastAetherPanSequence = 0;
        if (ShowFft && _fftEnabledAt == DateTime.MinValue) _fftEnabledAt = DateTime.UtcNow;
        SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
        _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
        if (!ShowFft) { Spectrum = null; _specAvg = null; _lastIqSpectrumSequence = 0; }
        _ = EnsureIqStreamAsync();
        _ = EnsureAudioStreamAsync();
    }

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

    void OnAudioPacket(uint streamId, float[] samples)
    {
        if (streamId != 0 && streamId == _daxStreamId) _audio.Push(samples);
    }

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
        _specAvg = null;
        Spectrum = ResliceAetherPan(aether);
    }
}
