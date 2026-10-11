using System.Collections.ObjectModel;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

/// <summary>Observed licence state only: never alters a radio's licensing.</summary>
public sealed partial class RadioViewModel
{
    public sealed record LicenseFeatureRow(string Name, string State, string Reason);
    public sealed record LicenseSubscriptionRow(string Name, string Expiration);

    readonly Dictionary<string, string> _licenseMetadata = new(StringComparer.OrdinalIgnoreCase);
    string _licenseConnectionText = "Connect a radio to inspect reported entitlements.";
    string _licenseVersionText = "Not reported";
    string _licenseIssuedText = "Not reported";
    string _licenseLastUpdate = "No licence status received";

    public ObservableCollection<LicenseFeatureRow> LicenseFeatures { get; } = new();
    public ObservableCollection<LicenseSubscriptionRow> LicenseSubscriptions { get; } = new();

    public string LicenseConnectionText
    {
        get => _licenseConnectionText;
        private set => Set(ref _licenseConnectionText, value);
    }
    public string LicenseVersionText
    {
        get => _licenseVersionText;
        private set => Set(ref _licenseVersionText, value);
    }
    public string LicenseIssuedText
    {
        get => _licenseIssuedText;
        private set => Set(ref _licenseIssuedText, value);
    }
    public string LicenseLastUpdate
    {
        get => _licenseLastUpdate;
        private set => Set(ref _licenseLastUpdate, value);
    }
    public string LicenseFeatureCount => LicenseFeatures.Count == 0
        ? "No feature status reported by this radio"
        : $"{LicenseFeatures.Count} radio-reported feature(s)";
    public string LicenseSubscriptionCount => LicenseSubscriptions.Count == 0
        ? "No subscription status reported"
        : $"{LicenseSubscriptions.Count} radio-reported subscription(s)";

    void SetLicenseSubscriptionResult(uint responseCode)
    {
        LicenseConnectionText = responseCode == 0
            ? "Licence status subscription accepted. Awaiting radio reports."
            : $"Licence subscription unavailable or refused (0x{responseCode:X8}). This is not evidence that features are disabled.";
    }

    void HandleLicenseStatus(string body)
    {
        if (!LicenseStatusDecoder.TryDecode(body, out var item) || item is null) return;
        LicenseConnectionText = "Receiving read-only licence status from the radio.";
        LicenseLastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        switch (item.Type)
        {
            case LicenseStatusDecoder.Kind.Feature:
            {
                if (string.IsNullOrWhiteSpace(item.Name)) return;
                string state = item.Enabled is true ? "Enabled"
                    : item.Enabled is false ? "Disabled" : "Not reported";
                var row = new LicenseFeatureRow(item.Name, state,
                    string.IsNullOrWhiteSpace(item.Reason) ? "Not supplied" : item.Reason);
                int index = -1;
                for (int i = 0; i < LicenseFeatures.Count; i++)
                    if (LicenseFeatures[i].Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                    { index = i; break; }
                if (index >= 0) LicenseFeatures[index] = row;
                else LicenseFeatures.Add(row);

                // Keep existing control diagnostics, but never infer disabled from an absent status.
                if (item.Enabled is bool enabled)
                    _licenseFeatures[item.Name] = (enabled, item.Reason);
                OnPropertyChanged(nameof(LicenseFeatureCount));
                OnPropertyChanged(nameof(LicenseSummary));
                break;
            }
            case LicenseStatusDecoder.Kind.Subscription:
            {
                if (string.IsNullOrWhiteSpace(item.Name)) return;
                var row = new LicenseSubscriptionRow(item.Name,
                    string.IsNullOrWhiteSpace(item.Expiration) ? "Not supplied" : item.Expiration);
                int index = -1;
                for (int i = 0; i < LicenseSubscriptions.Count; i++)
                    if (LicenseSubscriptions[i].Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                    { index = i; break; }
                if (index >= 0) LicenseSubscriptions[index] = row;
                else LicenseSubscriptions.Add(row);
                OnPropertyChanged(nameof(LicenseSubscriptionCount));
                break;
            }
            case LicenseStatusDecoder.Kind.Summary:
            {
                foreach (var (k, v) in item.Fields)
                    _licenseMetadata[k] = v;

                var lowest = _licenseMetadata.GetValueOrDefault("lowest_major_version");
                var highest = _licenseMetadata.GetValueOrDefault("highest_major_version");
                LicenseVersionText = highest is { Length: > 0 }
                    ? (lowest is { Length: > 0 } ? $"{lowest} – {highest}" : $"Up to {highest}")
                    : "Not reported";
                LicenseIssuedText = _licenseMetadata.GetValueOrDefault("issued") ??
                                    _licenseMetadata.GetValueOrDefault("issue_date") ??
                                    "Not reported";
                break;
            }
        }
    }

    void ClearLicenseView()
    {
        LicenseFeatures.Clear();
        LicenseSubscriptions.Clear();
        _licenseMetadata.Clear();
        LicenseConnectionText = "Connect a radio to inspect reported entitlements.";
        LicenseVersionText = "Not reported";
        LicenseIssuedText = "Not reported";
        LicenseLastUpdate = "No licence status received";
        OnPropertyChanged(nameof(LicenseFeatureCount));
        OnPropertyChanged(nameof(LicenseSubscriptionCount));
    }
}
