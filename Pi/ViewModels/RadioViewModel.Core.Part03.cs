using System.Collections.ObjectModel;
using Avalonia.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

    async Task ConnectCoreAsync(string host, int port, string title, string serial)
    {
        Cleanup();
        _wantConnected = true;
        _lastHost = host; _lastPort = port; _lastTitle = title; _lastSerial = serial ?? ""; _radioSerial = _lastSerial.Trim().Trim('"');
        IsConnecting = true;
        StatusText = $"Connecting to {host}:{port}";
        RadioTitle = string.IsNullOrWhiteSpace(title) ? host : title;
        LastMessage = "";

        var c = new FlexClient();
        c.Status += OnStatus;
        c.Message += OnRadioMessage;
        c.Disconnected += OnClientDisconnected;
        c.Audio += OnAudioPacket;
        c.Iq += OnIqPacket;
        c.MeterPacket += OnMeterPacket;
        try
        {
            await c.ConnectAsync(host, port);
            _client = c;
            IsConnected = true;
            _retrying = false;
            AetherPanBridge.Instance.FrameReceived += OnAetherPanFrame;

            await c.SendAsync("client program FlexCompanion");
            var info = await c.SendAsync("info");
            if (info.Code == 0) ApplyInfo(info.Message);

            var udp = await c.SendAsync($"client udpport {c.UdpPort}");
            if (udp.Code != 0)
                LastMessage = $"Meter stream registration failed ({FlexClient.ErrorText(udp.Code)}). Controls still work.";

            foreach (var sub in new[] { "sub client all", "sub slice all", "sub pan all", "sub tx all", "sub amplifier all", "sub license all" })
                await c.SendAsync(sub);

            // Do not subscribe to every meter. A typical FLEX session exposes dozens of meters,
            // most of which Companion never renders. Build metadata with `meter list` and subscribe
            // only to the selected slice LEVEL, PWR/SWR and the currently selected extra TX meter.
            await RefreshMeterSubscriptionsAsync();

            StatusText = $"Connected to {host}   handle {c.Handle}";
            _meterTimer.Start();
            _fftTimer.Start();
            OnConnectedExtras();
        }
        catch (Exception ex)
        {
            c.Status -= OnStatus;
            c.Message -= OnRadioMessage;
            c.Disconnected -= OnClientDisconnected;
            c.Audio -= OnAudioPacket;
            c.Iq -= OnIqPacket;
            c.MeterPacket -= OnMeterPacket;
            AetherPanBridge.Instance.FrameReceived -= OnAetherPanFrame;
            c.Dispose();
            _client = null;
            IsConnected = false;
            StatusText = ex is OperationCanceledException or TimeoutException
                ? $"No answer from {host}:{port}"
                : $"Connect failed: {ex.Message}";
            if (_retrying)
            {
                StatusText += "   retrying in 5 s";
                _reconnectTimer.Start();
            }
            else _wantConnected = false;
        }
        finally
        {
            IsConnecting = false;
            CommandManagerRefresh();
        }
    }

    public void Disconnect()
    {
        _wantConnected = false;
        _retrying = false;
        _reconnectTimer.Stop();
        Cleanup();
        StatusText = "Not connected";
        CommandManagerRefresh();
    }

    void OnClientDisconnected(string reason)
    {
        if (_client == null) return;
        Cleanup();
        if (_wantConnected)
        {
            _retrying = true;
            StatusText = $"Lost connection ({reason})   retrying in 5 s";
            _reconnectTimer.Start();
        }
        else StatusText = $"Disconnected ({reason})";
        CommandManagerRefresh();
    }

    void OnRadioMessage(string m) => LastMessage = m;

    void Cleanup()
    {
        _meterTimer.Stop();
        _fftTimer.Stop();
        _meterRefreshTimer.Stop();
        OnCleanupExtras();
        var c = _client;
        _client = null;
        if (c != null)
        {
            c.Status -= OnStatus;
            c.Message -= OnRadioMessage;
            c.Disconnected -= OnClientDisconnected;
            c.Audio -= OnAudioPacket;
            c.Iq -= OnIqPacket;
            c.MeterPacket -= OnMeterPacket;
            AetherPanBridge.Instance.FrameReceived -= OnAetherPanFrame;
            c.Dispose();
        }
        IsConnected = false;
        SelectedSlice = null;
        Slices.Clear();
        Stations.Clear();
        Stations.Add(StationItem.All);
        _selectedStation = StationItem.All;
        _boundClientId = "";
        OnPropertyChanged(nameof(SelectedStation));
        _clientStation.Clear();
        _meterDefs.Clear();
        _meterSubscriptions.Clear();
        _meterFallbackAll = false;
        _pans.Clear();
        _meterMapDirty = true;
        _lastMeterPacketUtc = DateTime.MinValue;
        Interlocked.Exchange(ref _meterUiPending, 0);
        _interlockTx = false;
        ResetPttOverrideMonitor();
        _licenseFeatures.Clear();
        AmplifierHandle = AmplifierModel = AmplifierIp = AmplifierState = "";
        AmplifierOperate = false;
        OnPropertyChanged(nameof(LicenseSummary));
        IsTransmitting = false;
        RxDbm = double.NaN; RxSText = "-"; RxDbmText = "";
        FwdWatts = 0; PowerText = "0 W"; Swr = 1; SwrText = "1.0:1";
        MicDb = -60; MicText = ""; CompDb = 0; CompText = "";
        foreach (var p in AllControls) { p.Reset(); p.ClearUnsupported(); }
        _autoTuneInt = false; OnPropertyChanged(nameof(AutoTuneIntermittent));
    }

    static void CommandManagerRefresh() => RelayCommand.InvalidateAll();

    void ApplyInfo(string msg)
    {
        var kv = Kv.Parse(Kv.Tokenize(msg.Replace(',', ' ')));
        var model = kv.GetValueOrDefault("model") ?? "";
        var name = kv.GetValueOrDefault("nickname") ?? kv.GetValueOrDefault("name") ?? "";
        var serial = kv.GetValueOrDefault("chassis_serial") ?? kv.GetValueOrDefault("serial") ?? "";
        serial = serial.Trim().Trim('"');
        if (serial.Length > 0) _radioSerial = serial;
        if (model.Length > 0)
            RadioTitle = name.Length > 0 && name != model ? $"{name}  ({model})" : model;
        PowerMax = model.StartsWith("AU", StringComparison.OrdinalIgnoreCase) ? 600 : 120;
    }

    // ───────────────────────── sending ─────────────────────────

    public async void Send(string cmd)
    {
        var c = _client;
        if (c == null) return;
        var (code, _) = await c.SendAsync(cmd);
        if (code != 0) LastMessage = $"{cmd}   {FlexClient.ErrorText(code)}";
    }

    const uint UnknownParameter = 0x5000002D;
    const uint CommandRefused = 0x50004001;
    const uint InvalidModeOrState = 0xE2000000;
    const uint InvalidDspForMode = 0x50000061;
    const uint InvalidCommandForMode = 0x50000085;

    static bool IsDigitalDspRestrictedMode(string mode)
    {
        mode = mode.Trim().ToUpperInvariant();
        return mode is "DIGU" or "DIGL" or "RTTY" or "FDV" or "FDVL" or "FDVU";
    }

    static bool IsCwMode(string mode)
    {
        mode = mode.Trim().ToUpperInvariant();
        return mode is "CW" or "CWL" or "CWR";
    }

    static string? LicenseFeatureForControl(string label) => label.ToUpperInvariant() switch
    {
        "NRF" or "NRL" or "NRS" or "RNN" or "ANFL" or "ANFT" => "NOISE_REDUCTION",
        _ => null,
    };

    string? ExplicitLicenseReason(ParamControl? ctl)
    {
        if (ctl == null) return null;
        var feature = LicenseFeatureForControl(ctl.Label);
        if (feature == null || !_licenseFeatures.TryGetValue(feature, out var state) || state.Enabled) return null;
        var source = string.IsNullOrWhiteSpace(state.Reason) ? "radio feature status" : state.Reason;
        return $"radio reports {feature} disabled ({source})";
    }
}
