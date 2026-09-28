using FlexCompanion.Station;

namespace FlexCompanion.ViewModels;

public sealed class SpotItem
{
    public required DxSpot Spot { get; init; }
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
    public string Call => Spot.Callsign;
    public string Frequency => $"{Spot.FrequencyMhz:0.000000}";
    public string Comment => Spot.Comment;
    public string Source => Spot.Spotter.Length > 0 ? $"de {Spot.Spotter}" : "DX";
    public string Age => $"{Math.Max(0, (int)(DateTime.UtcNow - ReceivedUtc).TotalMinutes)}m";
}
