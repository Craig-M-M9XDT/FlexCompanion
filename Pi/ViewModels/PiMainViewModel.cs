using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

/// <summary>
/// Avalonia/Raspberry Pi shell view model. The radio/session implementation is the same
/// FLEX API code as the Windows build; only the presentation layer is different.
/// </summary>
public sealed class PiMainViewModel : ObservableObject
{
    RadioInfo? _selectedRadio;
    bool _touchMode;
    bool _compactMode;
    bool _microMode;
    bool _kioskMode;

    public PiMainViewModel()
    {
        Settings = AppSettings.Load();
        SlotA.Prefs = Settings.PrefsFor("A");
        SlotB.Prefs = Settings.PrefsFor("B");
        SlotA.LowBandwidthMode = Settings.NetworkSaver;
        SlotB.LowBandwidthMode = Settings.NetworkSaver;
        Station = new StationViewModel(Settings, slot => slot == "B" ? SlotB : SlotA);

        Discovery.Changed += () =>
        {
            OnPropertyChanged(nameof(DiscoveryText));
            RelayCommand.InvalidateAll();
        };
        Discovery.Radios.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DiscoveryText));
        Discovery.Start();

        RescanCommand = new RelayCommand(Discovery.Rescan);
        ConnectSelectedCommand = new RelayCommand(async p => await ConnectSelectedAsync(p), _ => SelectedRadio != null);
        ConnectManualCommand = new RelayCommand(async p => await ConnectManualAsync(p), _ => ManualIp.Trim().Length > 0);
    }

    public AppSettings Settings { get; }
    public Discovery Discovery { get; } = new();
    public RadioViewModel SlotA { get; } = new("A");
    public RadioViewModel SlotB { get; } = new("B");
    public StationViewModel Station { get; }
    public IReadOnlyList<string> SlotNames { get; } = new[] { "A", "B" };

    public RelayCommand RescanCommand { get; }
    public RelayCommand ConnectSelectedCommand { get; }
    public RelayCommand ConnectManualCommand { get; }

    public string DiscoveryText => Discovery.Error
        ?? (Discovery.Radios.Count == 0 ? "Listening for FLEX radios on UDP 4992" : $"{Discovery.Radios.Count} radio(s) found");

    public RadioInfo? SelectedRadio
    {
        get => _selectedRadio;
        set
        {
            if (!Set(ref _selectedRadio, value)) return;
            RelayCommand.InvalidateAll();
        }
    }

    public string ManualIp
    {
        get => Settings.ManualIp;
        set
        {
            Settings.ManualIp = value ?? "";
            OnPropertyChanged();
            RelayCommand.InvalidateAll();
        }
    }

    public bool DualMode
    {
        get => Settings.DualMode;
        set
        {
            if (Settings.DualMode == value) return;
            Settings.DualMode = value;
            OnPropertyChanged();
        }
    }

    public bool NetworkSaver
    {
        get => Settings.NetworkSaver;
        set
        {
            if (Settings.NetworkSaver == value) return;
            Settings.NetworkSaver = value;
            SlotA.LowBandwidthMode = value;
            SlotB.LowBandwidthMode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Enabled automatically after the first real touch pointer event.</summary>
    public bool TouchMode
    {
        get => _touchMode;
        set => Set(ref _touchMode, value);
    }

    /// <summary>Width-driven responsive state: hides non-essential chrome and stacks radios.</summary>
    public bool CompactMode
    {
        get => _compactMode;
        set
        {
            if (!Set(ref _compactMode, value)) return;
            OnPropertyChanged(nameof(WideMode));
        }
    }

    public bool WideMode => !CompactMode;

    /// <summary>Very small screens such as 800x480 use the minimum touch layout.</summary>
    public bool MicroMode
    {
        get => _microMode;
        set => Set(ref _microMode, value);
    }

    public bool KioskMode
    {
        get => _kioskMode;
        set => Set(ref _kioskMode, value);
    }

    RadioViewModel SlotFor(object? p)
    {
        if (p as string == "B")
        {
            DualMode = true;
            return SlotB;
        }
        return SlotA;
    }

    async Task ConnectSelectedAsync(object? p)
    {
        var r = SelectedRadio;
        if (r == null) return;
        await SlotFor(p).ConnectAsync(r.Ip, r.Port, r.Title, r.Serial);
    }

    async Task ConnectManualAsync(object? p)
    {
        var text = ManualIp.Trim();
        if (text.Length == 0) return;
        int port = 4992;
        string host = text;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsedPort))
        {
            host = text[..colon];
            port = parsedPort;
        }
        await SlotFor(p).ConnectAsync(host, port, host);
    }

    public void Shutdown()
    {
        Settings.Save();
        Station.Dispose();
        SlotA.Disconnect();
        SlotB.Disconnect();
        Discovery.Dispose();
    }
}
