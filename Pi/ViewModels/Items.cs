using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed class SliceItem : ObservableObject
{
    string _station = "";

    public SliceItem(int index) => Index = index;

    public int Index { get; }
    public Dictionary<string, string> State { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Letter => State.GetValueOrDefault("index_letter") ?? ((char)('A' + Index)).ToString();
    public string Mode => State.GetValueOrDefault("mode") ?? "";
    public string Pan => State.GetValueOrDefault("pan") ?? "";
    public string ClientHandle => State.GetValueOrDefault("client_handle") ?? "";
    public bool Active => State.GetValueOrDefault("active") == "1";
    public bool IsDiversityChild => State.GetValueOrDefault("diversity_child") == "1";

    public string FreqText
    {
        get
        {
            var f = Kv.D(State.GetValueOrDefault("RF_frequency"));
            if (f == null) return "-";
            long hz = (long)Math.Round(f.Value * 1_000_000);
            return $"{hz / 1_000_000}.{hz / 1000 % 1000:000}.{hz % 1000:000}";
        }
    }

    public string Station { get => _station; set => Set(ref _station, value); }

    public void Merge(IReadOnlyDictionary<string, string> kv)
    {
        foreach (var p in kv) State[p.Key] = p.Value;
        OnPropertyChanged(string.Empty);
    }

    public override string ToString() => $"{Letter} {FreqText} {Mode}";
}

public sealed class StationItem
{
    public static readonly StationItem All = new() { Name = "All stations" };
    public string Handle { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Name { get; init; } = "";
    public override string ToString() => Name;
}

public sealed class MeterDef
{
    public int Id;
    public string Source = "";
    public int Num = -1;
    public string Name = "";
    public string Unit = "";
}
