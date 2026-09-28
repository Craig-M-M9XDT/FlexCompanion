using System.Collections.ObjectModel;
using Avalonia.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

    void HandleSlice(List<string> tok)
    {
        if (tok.Count < 2 || !int.TryParse(tok[1], out var idx)) return;
        var kv = Kv.Parse(tok.Skip(2));
        var s = Slices.FirstOrDefault(x => x.Index == idx);

        bool removed = (kv.TryGetValue("in_use", out var iu) && iu == "0") || tok.Contains("removed");
        if (removed)
        {
            if (s == null) return;
            bool wasSelected = s == SelectedSlice;
            Slices.Remove(s);
            if (wasSelected) SelectedSlice = Slices.FirstOrDefault(x => x.Active && StationMatches(x)) ?? Slices.FirstOrDefault();
            return;
        }

        if (s == null)
        {
            s = new SliceItem(idx);
            int pos = 0;
            while (pos < Slices.Count && Slices[pos].Index < idx) pos++;
            Slices.Insert(pos, s);
        }
        s.Merge(kv);
        if (_clientStation.TryGetValue(s.ClientHandle, out var st)) s.Station = st;

        if (FollowActiveSlice && kv.TryGetValue("active", out var a) && a == "1" && StationMatches(s) && s != SelectedSlice)
        {
            SelectedSlice = s;
            return;
        }
        if (SelectedSlice == null) { SelectedSlice = s; return; }

        if (s == SelectedSlice)
        {
            foreach (var c in AllControls.Where(x => x.Scope == "slice")) c.ApplyStatus(kv);
            if (kv.ContainsKey("pan") && _pans.TryGetValue(s.Pan, out var pan))
                foreach (var c in AllControls.Where(x => x.Scope == "pan")) c.ApplyStatus(pan);
            if (kv.ContainsKey("mode"))
            {
                OnPropertyChanged(nameof(SliceModeText));
                ApplyModeAvailability(announce: true);
            }
            if (kv.ContainsKey("mode") || kv.ContainsKey("pan") || kv.ContainsKey("RF_frequency"))
                ScheduleMeterRefresh();
            if (kv.ContainsKey("diversity_child")) OnPropertyChanged(nameof(EscNote));
            OnSelectedSliceStatus(kv);
        }
    }

    void HandlePan(List<string> tok)
    {
        var id = tok[2];
        if (tok.Contains("removed")) { _pans.Remove(id); return; }
        var kv = Kv.Parse(tok.Skip(3));
        if (!_pans.TryGetValue(id, out var state)) _pans[id] = state = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in kv) state[p.Key] = p.Value;
        if (SelectedSlice != null && string.Equals(SelectedSlice.Pan, id, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in AllControls.Where(x => x.Scope == "pan")) c.ApplyStatus(kv);
            OnSelectedPanStatus(kv);
        }
    }

    void HandleClient(List<string> tok)
    {
        var handle = tok[1];
        var kv = Kv.Parse(tok.Skip(2));

        if (tok.Contains("disconnected"))
        {
            _clientStation.Remove(handle);
            var gone = Stations.FirstOrDefault(x => x.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
            if (gone != null)
            {
                if (gone == SelectedStation) SelectedStation = StationItem.All;
                if (gone.ClientId == _boundClientId) _boundClientId = "";
                Stations.Remove(gone);
            }
            return;
        }

        var station = kv.GetValueOrDefault("station");
        var program = kv.GetValueOrDefault("program");
        if (station == null && program == null) return;
        var label = station ?? program!;
        _clientStation[handle] = label;
        foreach (var s in Slices.Where(x => x.ClientHandle.Equals(handle, StringComparison.OrdinalIgnoreCase)))
            s.Station = label;

        var clientId = kv.GetValueOrDefault("client_id");
        if (string.IsNullOrEmpty(clientId)) return;
        var name = program != null && station != null ? $"{station}  ({program})" : label;
        var existing = Stations.FirstOrDefault(x => x.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (existing != null && existing.Name == name) return;
        var item = new StationItem { Handle = handle, ClientId = clientId, Name = name };
        if (existing != null)
        {
            bool sel = existing == SelectedStation;
            Stations[Stations.IndexOf(existing)] = item;
            if (sel) { _selectedStation = item; OnPropertyChanged(nameof(SelectedStation)); }
        }
        else Stations.Add(item);
    }

    void HandleAmplifier(List<string> tok)
    {
        if (tok.Count < 2) return;
        var handle = tok[1];
        if (tok.Contains("removed"))
        {
            if (SameHandle(handle, AmplifierHandle))
            {
                AmplifierHandle = AmplifierModel = AmplifierIp = AmplifierState = "";
                AmplifierOperate = false;
                OnPropertyChanged(nameof(HasAmplifier));
            }
            return;
        }
        var kv = Kv.Parse(tok.Skip(2));
        var model = kv.GetValueOrDefault("model") ?? "";
        if (model.Equals("TunerGeniusXL", StringComparison.OrdinalIgnoreCase)) return;
        if (handle.Equals("0x00000000", StringComparison.OrdinalIgnoreCase)) handle = "";
        if (handle.Length > 0) AmplifierHandle = handle;
        if (model.Length > 0) AmplifierModel = model;
        if (kv.TryGetValue("ip", out var ip)) AmplifierIp = ip;
        if (kv.TryGetValue("state", out var state) && state.Length > 0)
        {
            AmplifierState = state;
            AmplifierOperate = !state.Equals("STANDBY", StringComparison.OrdinalIgnoreCase);
        }
        OnPropertyChanged(nameof(HasAmplifier));
    }

    void HandleLicense(List<string> tok)
    {
        if (tok.Count < 2 || !tok[1].Equals("feature", StringComparison.OrdinalIgnoreCase)) return;
        var kv = Kv.Parse(tok.Skip(2));
        var name = kv.GetValueOrDefault("name") ?? "";
        if (name.Length == 0) return;
        bool enabled = (kv.GetValueOrDefault("enabled") ?? "0") == "1";
        _licenseFeatures[name] = (enabled, kv.GetValueOrDefault("reason") ?? "");
        OnPropertyChanged(nameof(LicenseSummary));
    }

    void HandleInterlock(List<string> tok)
    {
        var kv = Kv.Parse(tok.Skip(1));
        if (kv.TryGetValue("state", out var st))
            _interlockTx = st.Equals("TRANSMITTING", StringComparison.OrdinalIgnoreCase);
    }

    string _boundClientId = "";

    StationItem? StationForCommands()
    {
        if (SelectedStation is { ClientId.Length: > 0 } chosen) return chosen;
        var handle = SelectedSlice?.ClientHandle ?? "";
        if (handle.Length > 0)
        {
            var owner = Stations.FirstOrDefault(x => x.ClientId.Length > 0 && SameHandle(x.Handle, handle));
            if (owner != null) return owner;
        }
        var gui = Stations.Where(x => x.ClientId.Length > 0).ToList();
        return gui.Count == 1 ? gui[0] : null;
    }

    static bool SameHandle(string a, string b)
    {
        static string N(string h) => (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? h[2..] : h).TrimStart('0').ToUpperInvariant();
        return N(a) == N(b);
    }
}
