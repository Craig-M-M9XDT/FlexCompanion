using System.Collections.ObjectModel;
using Avalonia.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

    async Task<bool> EnsureBoundAsync()
    {
        var c = _client;
        if (c == null) return false;
        var st = StationForCommands();
        if (st == null)
        {
            LastMessage = "No SmartSDR / AetherSDR station found to act for. Open the GUI on this radio, or pick it in the Station box.";
            return false;
        }
        if (st.ClientId == _boundClientId) return true;
        var (code, _) = await c.SendAsync($"client bind client_id={st.ClientId}");
        if (code != 0)
        {
            LastMessage = $"Could not attach to station {st.Name}: {FlexClient.ErrorText(code)}";
            return false;
        }
        _boundClientId = st.ClientId;
        return true;
    }

    async Task SendAsStationAsync(string cmd)
    {
        var c = _client;
        if (c == null || !await EnsureBoundAsync()) return;
        var (code, _) = await c.SendAsync(cmd);
        if (code != 0) LastMessage = $"{cmd}   {FlexClient.ErrorText(code)}";
    }

    void ScheduleMeterRefresh()
    {
        if (_client == null) return;
        _meterRefreshTimer.Stop();
        _meterRefreshTimer.Start();
    }

    async Task RefreshMeterSubscriptionsAsync()
    {
        var c = _client;
        if (c == null) return;
        if (!await _meterSubscriptionGate.WaitAsync(0))
        {
            ScheduleMeterRefresh();
            return;
        }
        try
        {
            c = _client;
            if (c == null) return;

            var list = await c.SendAsync("meter list");
            if (_client != c) return;
            if (list.Code != 0 || string.IsNullOrWhiteSpace(list.Message))
            {
                await EnableMeterFallbackAsync(c);
                return;
            }

            var body = list.Message.Trim();
            if (body.StartsWith("meter ", StringComparison.OrdinalIgnoreCase)) body = body[6..];
            _meterDefs.Clear();
            ParseMeterMetadata(body);
            if (_meterDefs.Count == 0)
            {
                await EnableMeterFallbackAsync(c);
                return;
            }

            _meterMapDirty = true;
            RebuildMeterMap();
            var desired = DesiredMeterIds();

            if (_meterFallbackAll)
            {
                await c.SendAsync("unsub meter all");
                _meterFallbackAll = false;
                _meterSubscriptions.Clear();
            }

            foreach (var id in _meterSubscriptions.Where(x => !desired.Contains(x)).ToArray())
            {
                await c.SendAsync($"unsub meter {id}");
                _meterSubscriptions.Remove(id);
            }
            foreach (var id in desired.Where(x => !_meterSubscriptions.Contains(x)))
            {
                var r = await c.SendAsync($"sub meter {id}");
                if (r.Code == 0) _meterSubscriptions.Add(id);
            }
        }
        finally
        {
            _meterSubscriptionGate.Release();
        }
    }

    HashSet<int> DesiredMeterIds()
    {
        var ids = new HashSet<int>();
        void Add(int id) { if (id >= 0) ids.Add(id); }

        Add(_rxId);
        Add(_fwdId);
        Add(_swrId);

        switch (TxMeter)
        {
            case "Mic": Add(_micId); break;
            case "Proc": Add(_compId); break;
            case "Vdd": Add(_vddId); break;
            case "Current": Add(_ampsId); break;
            case "Temp": Add(_tempId); break;
        }
        return ids;
    }

    async Task EnableMeterFallbackAsync(FlexClient c)
    {
        if (_meterFallbackAll || _client != c) return;
        var r = await c.SendAsync("sub meter all");
        if (r.Code == 0)
        {
            _meterFallbackAll = true;
            _meterSubscriptions.Clear();
            LastMessage = "Selective meter subscription wasn't available, so Companion fell back to all meters.";
        }
    }

    void HandleMeterStatus(string rest)
    {
        rest = rest.Trim();
        if (rest.EndsWith("removed", StringComparison.Ordinal))
        {
            foreach (var part in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var rid))
                {
                    _meterDefs.Remove(rid);
                    _meterSubscriptions.Remove(rid);
                }
            _meterMapDirty = true;
            ScheduleMeterRefresh();
            return;
        }

        ParseMeterMetadata(rest);
        _meterMapDirty = true;
    }

    void ParseMeterMetadata(string rest)
    {
        foreach (var part in rest.Split('#', StringSplitOptions.RemoveEmptyEntries))
        {
            int dot = part.IndexOf('.');
            int eq = part.IndexOf('=');
            if (dot <= 0 || eq < dot) continue;
            if (!int.TryParse(part[..dot], out var id)) continue;
            var key = part[(dot + 1)..eq];
            var val = part[(eq + 1)..];
            if (!_meterDefs.TryGetValue(id, out var d)) _meterDefs[id] = d = new MeterDef { Id = id };
            switch (key)
            {
                case "src": d.Source = val; break;
                case "num": int.TryParse(val, out d.Num); break;
                case "nam": d.Name = val; break;
                case "unit": d.Unit = val; break;
            }
        }
    }
}
