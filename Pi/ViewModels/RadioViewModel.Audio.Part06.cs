using System.Globalization;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

    void SuppressAgcFilters(SliceItem s)
    {
        _agcFilterSnapshot.Clear();

        foreach (var (statusKey, setKey) in AgcFilterKeys)
        {
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
        RestoreAgcFilters();
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
            _agc.Stop();
            RestoreAgcFilters();
            AgcChanged();
        }
        else if (_agc.Recommended >= 0)
        {
            _agc.Keep();
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
