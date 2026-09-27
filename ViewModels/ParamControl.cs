using System.Globalization;
using System.Windows.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

/// <summary>
/// One row in the UI: an on/off toggle and/or a level slider mapped to radio keys.
/// Set-keys and status-keys are separate because SmartSDR+ features are written with one
/// name and reported with another (e.g. set "lms_nr", status "nrl").
/// </summary>
public sealed class ParamControl : ObservableObject
{
    public string Label { get; init; } = "";
    public string Tooltip { get; init; } = "";
    /// <summary>"slice" -> slice set N ..., "pan" -> display pan set 0x.. ...</summary>
    public string Scope { get; init; } = "slice";

    public string? ToggleSetKey { get; init; }
    public string? ToggleStatusKey { get; init; }
    public Func<bool, string> ToggleFormat { get; init; } = b => b ? "1" : "0";

    public string? LevelSetKey { get; init; }
    public string? LevelStatusKey { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    public double DefaultLevel { get; init; } = 50;
    public Func<double, string> LevelFormat { get; init; } = v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
    public Func<string, double?> LevelParse { get; init; } = s => Kv.D(s);
    public Func<double, string> LevelDisplay { get; init; } = v => v.ToString("0", CultureInfo.CurrentCulture);
    public string NoLevelNote { get; init; } = "on / off only";

    /// <summary>When true the row is dimmed until the radio reports the key (feature missing / unlicensed).</summary>
    public bool RequiresReport { get; init; } = true;

    public Action<string, string>? Sender { get; set; }

    public bool HasToggle => ToggleSetKey != null;
    public bool NoToggle => !HasToggle;
    public bool HasLevel => LevelSetKey != null;
    public bool NoLevel => !HasLevel;
    public double LargeStep => Step * 10;

    bool _isOn;
    double _level = 50;
    bool _reported;
    bool _suppress;
    DispatcherTimer? _throttle;
    double _lastSentLevel = double.NaN;
    DateTime _lastLocalChange = DateTime.MinValue;

    public bool IsReported => !_unsupported && !_temporaryUnavailable && (_reported || !RequiresReport);

    bool _unsupported;
    bool _temporaryUnavailable;
    string _temporaryReason = "";

    /// <summary>True when the feature is valid on the radio but temporarily unavailable in the current mode/state.</summary>
    public bool IsTemporarilyUnavailable => _temporaryUnavailable;
    public string TemporaryReason => _temporaryReason;
    public string UnavailableText => _temporaryUnavailable && _temporaryReason.Length > 0
        ? _temporaryReason
        : _unsupported
            ? "Unsupported by this radio / firmware for this connection."
            : "Not reported by this radio (model, licence or firmware), so it can't be changed here.";

    /// <summary>The radio rejected this setting as unknown (0x5000002D): dim it until the next connect.</summary>
    public void MarkUnsupported()
    {
        if (_unsupported) return;
        _unsupported = true;
        _throttle?.Stop();
        _suppress = true;
        try { IsOn = false; } finally { _suppress = false; }
        OnPropertyChanged(nameof(IsReported));
        OnPropertyChanged(nameof(UnavailableText));
    }

    public void ClearUnsupported()
    {
        if (!_unsupported) return;
        _unsupported = false;
        OnPropertyChanged(nameof(IsReported));
        OnPropertyChanged(nameof(UnavailableText));
    }

    /// <summary>Temporarily gate a control for the selected mode without poisoning its radio capability state.</summary>
    public void SetTemporaryUnavailable(bool unavailable, string reason = "")
    {
        reason = unavailable ? reason : "";
        if (_temporaryUnavailable == unavailable && _temporaryReason == reason) return;
        _temporaryUnavailable = unavailable;
        _temporaryReason = reason;
        _throttle?.Stop();
        OnPropertyChanged(nameof(IsTemporarilyUnavailable));
        OnPropertyChanged(nameof(TemporaryReason));
        OnPropertyChanged(nameof(UnavailableText));
        OnPropertyChanged(nameof(IsReported));
    }

    public bool Uses(string key) =>
        string.Equals(key, ToggleSetKey, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, LevelSetKey, StringComparison.OrdinalIgnoreCase);

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value)) return;
            if (!_suppress && ToggleSetKey != null && IsReported)
                Sender?.Invoke(Scope, $"{ToggleSetKey}={ToggleFormat(value)}");
        }
    }

    public double Level
    {
        get => _level;
        set
        {
            var v = Math.Clamp(Math.Round((value - Min) / Step) * Step + Min, Min, Max);
            if (Math.Abs(v - _level) < 1e-9) return;
            _level = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LevelText));
            if (!_suppress && LevelSetKey != null && IsReported)
            {
                _lastLocalChange = DateTime.UtcNow;
                QueueLevelSend();
            }
        }
    }

    public string LevelText => HasLevel ? LevelDisplay(_level) : "";

    // Sends immediately, then at most every 70 ms while the slider is moving, then once more at rest.
    void QueueLevelSend()
    {
        if (_throttle == null)
        {
            _throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
            _throttle.Tick += (_, _) =>
            {
                if (Math.Abs(_level - _lastSentLevel) > 1e-9) SendLevel();
                else _throttle.Stop();
            };
        }
        if (!_throttle.IsEnabled)
        {
            SendLevel();
            _throttle.Start();
        }
    }

    void SendLevel()
    {
        _lastSentLevel = _level;
        Sender?.Invoke(Scope, $"{LevelSetKey}={LevelFormat(_level)}");
    }

    /// <summary>Apply a status update from the radio without echoing it back.</summary>
    public void ApplyStatus(IReadOnlyDictionary<string, string> kv)
    {
        _suppress = true;
        try
        {
            if (ToggleStatusKey != null && kv.TryGetValue(ToggleStatusKey, out var t))
            {
                IsOn = t == "1" || t.Equals("on", StringComparison.OrdinalIgnoreCase)
                               || t.Equals("true", StringComparison.OrdinalIgnoreCase);
                MarkReported();
            }
            if (LevelStatusKey != null && kv.TryGetValue(LevelStatusKey, out var l))
            {
                // Ignore the radio's echo while the user is still dragging.
                if ((DateTime.UtcNow - _lastLocalChange).TotalMilliseconds > 600)
                {
                    var v = LevelParse(l);
                    if (v.HasValue) Level = v.Value;
                }
                MarkReported();
            }
        }
        finally { _suppress = false; }
    }

    void MarkReported()
    {
        if (_reported) return;
        _reported = true;
        OnPropertyChanged(nameof(IsReported));
    }

    public void Reset()
    {
        _suppress = true;
        try
        {
            IsOn = false;
            Level = DefaultLevel;
            _lastSentLevel = double.NaN;
            _throttle?.Stop();
            if (_temporaryUnavailable || _temporaryReason.Length > 0)
            {
                _temporaryUnavailable = false;
                _temporaryReason = "";
                OnPropertyChanged(nameof(IsTemporarilyUnavailable));
                OnPropertyChanged(nameof(TemporaryReason));
                OnPropertyChanged(nameof(UnavailableText));
                OnPropertyChanged(nameof(IsReported));
            }
            if (_reported) { _reported = false; OnPropertyChanged(nameof(IsReported)); }
        }
        finally { _suppress = false; }
    }
}
