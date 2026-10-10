using System.IO;
using System.Text.Json;

namespace FlexCompanion.Services;

public sealed class StationMacro
{
    public string Label { get; set; } = "Macro";
    /// <summary>One FLEX command per line. Lines beginning with # are ignored.</summary>
    public string Commands { get; set; } = "";
}

/// <summary>Per radio-slot display preferences.</summary>
public sealed class SlotPrefs
{
    /// <summary>Legacy v0.5 setting retained so existing settings migrate cleanly.</summary>
    public bool AnalogueMeter { get; set; } = true;
    /// <summary>Simple Analogue, Digital Select, Multi Analogue or Digital Multimeter.</summary>
    public string MeterMode { get; set; } = "";
    /// <summary>Power, SWR, Proc, Mic, Vdd, Current, Temp</summary>
    public string TxMeter { get; set; } = "Power";
    public string MultiNeedle1 { get; set; } = "Power";
    public string MultiNeedle2 { get; set; } = "SWR";
    public string MultiNeedle3 { get; set; } = "Current";
    public bool ShowFft { get; set; }
    /// <summary>Displayed FFT width, kHz (3, 6, 12, 24, 48, 96 or 192).</summary>
    public double FftSpanKhz { get; set; } = 48;
    public double AgcTargetDb { get; set; } = -28;
    /// <summary>Automatically disable hardware-mic PTT override on radios that advertise the API when PC mic is selected.</summary>
    public bool AutoPreservePcPttAudio { get; set; } = true;
}

/// <summary>Stored as JSON in %AppData%\FlexCompanion\settings.json.</summary>
public sealed class AppSettings
{
    public string? WallpaperPath { get; set; }
    public double WallpaperDim { get; set; } = 0.55;
    public bool DualMode { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool ShowSidebar { get; set; } = true;
    /// <summary>Reduces Companion network/CPU load for dual-radio operation. Caps DAX IQ at 24 kHz and slows UI refresh.</summary>
    public bool NetworkSaver { get; set; }
    public string ManualIp { get; set; } = "";
    public Dictionary<string, SlotPrefs> Slots { get; set; } = new();

    public SlotPrefs PrefsFor(string slot)
    {
        if (!Slots.TryGetValue(slot, out var p)) Slots[slot] = p = new SlotPrefs();
        return p;
    }

    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 860;

    // Native station-tools settings. FRStack is no longer required.
    public string StationTargetSlot { get; set; } = "A";
    public string DxClusterHost { get; set; } = "";
    public int DxClusterPort { get; set; } = 7300;
    public string DxClusterCallsign { get; set; } = "";
    public bool DxClusterAutoReconnect { get; set; } = true;
    public int SpotMaxAgeMinutes { get; set; } = 30;
    public bool PublishSpotsToRadio { get; set; } = true;
    public string PgxlHost { get; set; } = "";
    public int PgxlPort { get; set; } = 9008;
    public bool PgxlAutoReconnect { get; set; } = true;
    public List<StationMacro> StationMacros { get; set; } = new()
    {
        new() { Label = "USB", Commands = "@mode USB" },
        new() { Label = "LSB", Commands = "@mode LSB" },
        new() { Label = "CW", Commands = "@mode CW" },
        new() { Label = "DIGU", Commands = "@mode DIGU" },
    };

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlexCompanion");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* settings are best effort */ }
    }
}
