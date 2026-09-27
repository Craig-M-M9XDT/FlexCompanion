using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    RadioInfo? _selectedRadio;
    ImageSource? _wallpaper;

    public MainViewModel()
    {
        Settings = AppSettings.Load();
        Station = new StationViewModel(Settings, slot => slot == "B" ? SlotB : SlotA);
        SlotA.Prefs = Settings.PrefsFor("A");
        SlotB.Prefs = Settings.PrefsFor("B");
        SlotA.LowBandwidthMode = Settings.NetworkSaver;
        SlotB.LowBandwidthMode = Settings.NetworkSaver;
        LoadWallpaper(Settings.WallpaperPath);

        Discovery.Changed += () => OnPropertyChanged(nameof(DiscoveryText));
        Discovery.Radios.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DiscoveryText));
        Discovery.Start();

        RescanCommand = new RelayCommand(Discovery.Rescan);
        ConnectSelectedCommand = new RelayCommand(async p => await ConnectSelectedAsync(p), _ => SelectedRadio != null);
        ConnectManualCommand = new RelayCommand(async p => await ConnectManualAsync(p), _ => ManualIp.Trim().Length > 0);
        ChooseWallpaperCommand = new RelayCommand(ChooseWallpaper);
        ClearWallpaperCommand = new RelayCommand(() => { Settings.WallpaperPath = null; LoadWallpaper(null); });
    }

    public AppSettings Settings { get; }
    public Discovery Discovery { get; } = new();
    public RadioViewModel SlotA { get; } = new("A");
    public RadioViewModel SlotB { get; } = new("B");
    public StationViewModel Station { get; }

    public RelayCommand RescanCommand { get; }
    public RelayCommand ConnectSelectedCommand { get; }
    public RelayCommand ConnectManualCommand { get; }
    public RelayCommand ChooseWallpaperCommand { get; }
    public RelayCommand ClearWallpaperCommand { get; }

    public string DiscoveryText => Discovery.Error
        ?? (Discovery.Radios.Count == 0 ? "Listening for radios on UDP 4992" : $"{Discovery.Radios.Count} radio(s) found");

    public RadioInfo? SelectedRadio { get => _selectedRadio; set => Set(ref _selectedRadio, value); }

    public string ManualIp
    {
        get => Settings.ManualIp;
        set { Settings.ManualIp = value; OnPropertyChanged(); }
    }

    public bool DualMode
    {
        get => Settings.DualMode;
        set { Settings.DualMode = value; OnPropertyChanged(); }
    }

    public bool AlwaysOnTop
    {
        get => Settings.AlwaysOnTop;
        set { Settings.AlwaysOnTop = value; OnPropertyChanged(); }
    }

    public bool ShowSidebar
    {
        get => Settings.ShowSidebar;
        set { Settings.ShowSidebar = value; OnPropertyChanged(); }
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

    public double WallpaperDim
    {
        get => Settings.WallpaperDim;
        set { Settings.WallpaperDim = value; OnPropertyChanged(); }
    }

    public ImageSource? WallpaperImage { get => _wallpaper; private set => Set(ref _wallpaper, value); }

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
        var host = text;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p2)) { host = text[..colon]; port = p2; }
        await SlotFor(p).ConnectAsync(host, port, host);
    }

    void ChooseWallpaper()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a wallpaper",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        Settings.WallpaperPath = dlg.FileName;
        LoadWallpaper(dlg.FileName);
    }

    void LoadWallpaper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { WallpaperImage = null; return; }
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // don't lock the file
            bi.DecodePixelWidth = 2560;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            WallpaperImage = bi;
        }
        catch { WallpaperImage = null; }
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
