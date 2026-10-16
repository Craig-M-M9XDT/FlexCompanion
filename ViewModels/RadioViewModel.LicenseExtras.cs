using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FlexCompanion.ViewModels;

/// <summary>Presentation and export of observed radio entitlements only.</summary>
public sealed partial class RadioViewModel
{
    public sealed record LicenseFeatureGroup(string Name, IReadOnlyList<LicenseFeatureRow> Features);

    public ObservableCollection<LicenseFeatureGroup> LicenseFeatureGroups { get; } = new();

    public string LicenseCounts =>
        $"{LicenseFeatures.Count} reported  •  {LicenseFeatures.Count(x => x.State == "Enabled")} enabled  •  " +
        $"{LicenseFeatures.Count(x => x.State == "Disabled")} disabled";

    public RelayCommand ExportLicenseCsvCommand => _exportCsv ??=
        new RelayCommand(() => ExportLicense("csv"), () => LicenseFeatures.Count > 0);
    public RelayCommand ExportLicenseJsonCommand => _exportJson ??=
        new RelayCommand(() => ExportLicense("json"), () => LicenseFeatures.Count > 0);
    RelayCommand? _exportCsv;
    RelayCommand? _exportJson;

    static string GroupFeature(LicenseFeatureRow row)
    {
        if (row.State == "Enabled" && row.Reason.Equals("LICENSE_FILE", StringComparison.OrdinalIgnoreCase))
            return "Individually unlocked";
        if (row.Reason.Equals("BUILT_IN", StringComparison.OrdinalIgnoreCase))
            return "Included with licence";
        if (row.Reason.Equals("PLUS", StringComparison.OrdinalIgnoreCase))
            return "SmartSDR+ features";
        if (row.Reason.Equals("EA", StringComparison.OrdinalIgnoreCase))
            return "Early Access";
        return "Other / licence-file features";
    }

    void RebuildLicenseFeatureGroups()
    {
        var order = new[] {
            "Included with licence", "Individually unlocked", "SmartSDR+ features",
            "Early Access", "Other / licence-file features"
        };
        LicenseFeatureGroups.Clear();
        foreach (var group in order)
        {
            var matches = LicenseFeatures.Where(f => GroupFeature(f) == group)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length > 0)
                LicenseFeatureGroups.Add(new LicenseFeatureGroup($"{group}  ({matches.Length})", matches));
        }
        OnPropertyChanged(nameof(LicenseCounts));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // Prevent a radio-supplied field beginning with a spreadsheet formula from being
    // evaluated if the operator opens the CSV in Excel or another spreadsheet app.
    static string Csv(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@'
                             || v[0] is '\t' or '\r' or '\n'))
            v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    void ExportLicense(string extension)
    {
        if (LicenseFeatures.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export reported SmartSDR licence status",
            Filter = extension == "csv" ? "CSV file (*.csv)|*.csv" : "JSON file (*.json)|*.json",
            DefaultExt = "." + extension,
            AddExtension = true,
            FileName = $"FlexCompanion-Radio{Slot}-Licence-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (extension == "csv")
            {
                var csv = new StringBuilder();
                csv.AppendLine("radio_slot,radio_title,major_versions,feature,state,reason,group");
                foreach (var f in LicenseFeatures.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    csv.AppendLine(string.Join(",", new[] { Slot, RadioTitle, LicenseVersionText,
                        f.Name, f.State, f.Reason, GroupFeature(f) }.Select(Csv)));
                File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            else
            {
                var snapshot = new
                {
                    source = "Flex Control Companion — radio reported; read-only",
                    captured_at_local = DateTimeOffset.Now,
                    slot = Slot,
                    radio = RadioTitle,
                    licence_major_versions = LicenseVersionText,
                    licence_issued = LicenseIssuedText,
                    reported_last_update = LicenseLastUpdate,
                    metadata = new Dictionary<string, string>(_licenseMetadata),
                    features = LicenseFeatures.OrderBy(f => f.Name).Select(f => new
                    {
                        name = f.Name,
                        state = f.State,
                        reason = f.Reason,
                        group = GroupFeature(f)
                    }).ToArray(),
                    subscriptions = LicenseSubscriptions.Select(s => new { name = s.Name, expires = s.Expiration }).ToArray(),
                    note = "Absent fields are not disabled; radio controls entitlements."
                };
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                { WriteIndented = true }), Encoding.UTF8);
            }
            LicenseConnectionText = $"Status exported to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            LicenseConnectionText = $"Licence export failed: {ex.Message}";
        }
    }
}
