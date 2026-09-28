using System.Collections.ObjectModel;
using Avalonia.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

    void ApplyModeAvailability(bool announce = false)
    {
        // Temporary mode gating is deliberately separate from permanent capability detection.
        // AetherSDR follows the same UI rule: ANF/ANFL/ANFT are hidden in DIG/RTTY/FDV;
        // CW also excludes those notch filters and RNN. Returning to a compatible mode restores them.
        foreach (var ctl in AllControls) ctl.SetTemporaryUnavailable(false);

        var mode = SelectedSlice?.Mode.Trim().ToUpperInvariant() ?? "";
        if (mode.Length == 0) return;

        string[] blocked = IsDigitalDspRestrictedMode(mode)
            ? ["ANF", "ANFL", "ANFT"]
            : IsCwMode(mode) ? ["ANF", "RNN", "ANFL", "ANFT"] : [];

        foreach (var label in blocked)
        {
            var ctl = AllControls.FirstOrDefault(x => x.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            ctl?.SetTemporaryUnavailable(true, $"{label} is unavailable in {mode} mode");
        }

        if (announce && blocked.Length > 0)
            LastMessage = $"{string.Join(" / ", blocked)} unavailable in {mode} mode. They will be restored automatically when you return to a compatible mode.";
        else if (announce && blocked.Length == 0
                 && LastMessage.Contains("unavailable in", StringComparison.OrdinalIgnoreCase)
                 && LastMessage.Contains("mode", StringComparison.OrdinalIgnoreCase))
            LastMessage = $"DSP mode restriction cleared — filters are available again in {mode}.";
    }

    string DescribeControlError(ParamControl? ctl, string key, uint code)
    {
        var label = ctl?.Label ?? key.ToUpperInvariant();
        var mode = SelectedSlice?.Mode.Trim().ToUpperInvariant() ?? "";
        var hex = $"0x{code:X8}";
        var licence = ExplicitLicenseReason(ctl);

        if (code == UnknownParameter)
            return licence != null
                ? $"{label} unavailable — {licence} [{hex}]"
                : $"{label} unavailable — this radio / firmware does not report support for {key} [{hex}]";

        if (code == CommandRefused || code == InvalidModeOrState || code == InvalidDspForMode || code == InvalidCommandForMode)
        {
            if (ctl?.IsTemporarilyUnavailable == true && ctl.TemporaryReason.Length > 0)
                return $"{ctl.TemporaryReason} [{hex}]";
            if (licence != null)
                return $"{label} unavailable — {licence} [{hex}]";
            if (mode.Length > 0)
                return $"{label} unavailable in {mode} mode or the radio's current state [{hex}]";
            return $"{label} unavailable in the radio's current state [{hex}]";
        }

        return $"{label}: {FlexClient.ErrorText(code)} [{hex}]";
    }

    async void SendScoped(string scope, string kv)
    {
        var s = SelectedSlice;
        var c = _client;
        if (s == null || c == null) return;
        string cmd;
        if (scope == "pan")
        {
            if (s.Pan.Length == 0) return;
            cmd = $"display pan set {s.Pan} {kv}";
        }
        else cmd = $"slice set {s.Index} {kv}";

        // Normal DSP writes are legal from a non-GUI client, but when the slice belongs
        // to a GUI station we bind first so SmartSDR/Aether and this companion share the
        // same MultiFLEX context. Failure to bind does not block ordinary slice commands.
        await TryBindForSelectedSliceAsync();
        var key = kv.Split('=')[0];
        var ctl = AllControls.FirstOrDefault(x => x.Scope == scope && x.Uses(key));
        if (ctl?.IsTemporarilyUnavailable == true)
        {
            LastMessage = ctl.TemporaryReason.Length > 0
                ? ctl.TemporaryReason
                : $"{ctl.Label} is temporarily unavailable in the current mode.";
            return;
        }

        var (code, _) = await c.SendAsync(cmd);
        if (code == 0) return;
        if (code == UnknownParameter && ctl != null)
        {
            // Unknown parameter is a genuine capability result and may be cached for this connection.
            // Context/state errors below must never poison capability state.
            ctl.MarkUnsupported();
        }
        LastMessage = DescribeControlError(ctl, key, code);
    }

    void ReapplyAll()
    {
        foreach (var c in AllControls) c.Reset();
        var s = SelectedSlice;
        if (s == null) return;
        foreach (var c in AllControls.Where(x => x.Scope == "slice")) c.ApplyStatus(s.State);
        if (_pans.TryGetValue(s.Pan, out var pan))
            foreach (var c in AllControls.Where(x => x.Scope == "pan")) c.ApplyStatus(pan);
        _autoTuneInt = false;
        OnPropertyChanged(nameof(AutoTuneIntermittent));
    }

    // ───────────────────────── status parsing ─────────────────────────


    /// <summary>Execute a raw FLEX command against this radio and return the radio response.</summary>
    public async Task<(uint Code, string Message)> ExecuteAsync(string cmd, bool bindToSelectedStation = false)
    {
        var c = _client;
        if (c == null) return (0xFFFFFFFF, "not connected");
        if (bindToSelectedStation) await TryBindForSelectedSliceAsync();
        var result = await c.SendAsync(cmd);
        if (result.Code != 0) LastMessage = $"{cmd}   {FlexClient.ErrorText(result.Code)}";
        return result;
    }

    public Task<(uint Code, string Message)> TuneAsync(double mhz)
        => SelectedSlice == null ? Task.FromResult((0xFFFFFFFFu, "no slice selected"))
                                 // No autopan=0: band jumps and spot clicks must let the panadapter follow the slice.
                                 : ExecuteAsync($"slice tune {SelectedSlice.Index} {mhz.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}", true);

    public Task<(uint Code, string Message)> SetModeAsync(string mode)
        => SelectedSlice == null ? Task.FromResult((0xFFFFFFFFu, "no slice selected"))
                                 : ExecuteAsync($"slice set {SelectedSlice.Index} mode={mode}", true);

    public Task<(uint Code, string Message)> SetAmplifierOperateAsync(bool on)
        => AmplifierHandle.Length == 0 ? Task.FromResult((0xFFFFFFFFu, "no amplifier reported by radio"))
                                       : ExecuteAsync($"amplifier set {AmplifierHandle} operate={(on ? 1 : 0)}");

    async Task<bool> TryBindForSelectedSliceAsync()
    {
        var c = _client;
        if (c == null) return false;
        var st = StationForCommands();
        if (st == null || st.ClientId.Length == 0) return false;
        if (st.ClientId == _boundClientId) return true;
        var (code, _) = await c.SendAsync($"client bind client_id={st.ClientId}");
        if (code != 0) return false;
        _boundClientId = st.ClientId;
        return true;
    }

    void OnStatus(string body)
    {
        if (body.StartsWith("meter ", StringComparison.Ordinal)) { HandleMeterStatus(body[6..]); return; }

        var tok = Kv.Tokenize(body);
        if (tok.Count == 0) return;
        switch (tok[0])
        {
            case "slice": HandleSlice(tok); break;
            case "display" when tok.Count > 2 && tok[1] == "pan": HandlePan(tok); break;
            case "client" when tok.Count > 1: HandleClient(tok); break;
            case "interlock": HandleInterlock(tok); break;
            case "amplifier": HandleAmplifier(tok); break;
            case "license": HandleLicense(tok); break;
        }
    }

    bool StationMatches(SliceItem s) =>
        SelectedStation == null || SelectedStation.Handle.Length == 0 ||
        string.Equals(s.ClientHandle, SelectedStation.Handle, StringComparison.OrdinalIgnoreCase);
}
