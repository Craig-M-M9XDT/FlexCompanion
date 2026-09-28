using System.Globalization;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

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

        if (TryGetFreshAetherFrame(out _))
        {
            await RemoveIqStreamAsync(clearSpectrum: false);
            SpectrumStatus = "Aether shared pan · radio FFT · no extra DAX IQ stream";
            return;
        }

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
            if (panCh < 0 || panCh == _iqChannel) return;
            if (panCh == 0)
            {
                _iqLost = true;
                _iqPanAssigned = false;
                await RemoveIqStreamAsync();
                SpectrumStatus = "Another program removed this pan's DAX IQ channel. Toggle FFT off/on to take it again.";
                return;
            }
            _iqPanAssigned = false;
        }
        if (_iqLost && string.Equals(_iqPanId, pan, StringComparison.OrdinalIgnoreCase)) return;
        _iqLost = false;

        await RemoveIqStreamAsync();

        int ch = 0;
        bool assigned = false;
        if (_pans.TryGetValue(pan, out var panState) && int.TryParse(panState.GetValueOrDefault("daxiq_channel"), out var existing) && existing is >= 1 and <= 4)
        {
            ch = existing;
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

    void OnIqPacket(uint streamId, float[] i, float[] q, int count)
    {
        if (streamId != 0 && streamId == _iqStreamId) _iq.Push(i, q, count);
    }

    string SpectrumStatusText(int channel)
    {
        if (!LowBandwidthMode || FftSpanKhz <= 24)
            return $"DAX IQ {channel} · {EffectiveFftSpanKhz:0} kHz span";
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
}
