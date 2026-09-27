using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using FlexCompanion.ViewModels;

namespace FlexCompanion.Flex;

/// <summary>One radio seen on the LAN.</summary>
public sealed class RadioInfo : ObservableObject
{
    string _model = "", _nickname = "", _ip = "", _version = "", _status = "", _callsign = "", _stations = "";

    public string Serial { get; init; } = "";
    public int Port { get; set; } = 4992;
    public DateTime LastSeen { get; set; }

    public string Model { get => _model; set { if (Set(ref _model, value)) OnPropertyChanged(nameof(Title)); } }
    public string Nickname { get => _nickname; set { if (Set(ref _nickname, value)) OnPropertyChanged(nameof(Title)); } }
    public string Ip { get => _ip; set { if (Set(ref _ip, value)) OnPropertyChanged(nameof(Detail)); } }
    public string Version { get => _version; set { if (Set(ref _version, value)) OnPropertyChanged(nameof(Detail)); } }
    public string Status { get => _status; set { if (Set(ref _status, value)) OnPropertyChanged(nameof(Detail)); } }
    public string Callsign { get => _callsign; set { if (Set(ref _callsign, value)) OnPropertyChanged(nameof(Title)); } }
    public string Stations { get => _stations; set { if (Set(ref _stations, value)) OnPropertyChanged(nameof(Detail)); } }

    public string Title
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Nickname) ? Model : $"{Nickname}  ({Model})";
            return string.IsNullOrWhiteSpace(Callsign) ? name : $"{name}  {Callsign}";
        }
    }

    public string Detail
    {
        get
        {
            var s = $"{Ip}   v{Version}   {Status}";
            return string.IsNullOrWhiteSpace(Stations) ? s : $"{s}   in use by {Stations}";
        }
    }
}

/// <summary>
/// Listens for the discovery broadcasts every FLEX-6000/8000/Aurora sends on UDP 4992.
/// The socket is opened with address reuse so it can coexist with SmartSDR on the same PC.
/// </summary>
public sealed class Discovery : IDisposable
{
    public const int Port = 4992;

    Socket? _sock;
    CancellationTokenSource? _cts;
    readonly DispatcherTimer _purge = new() { Interval = TimeSpan.FromSeconds(3) };

    public ObservableCollection<RadioInfo> Radios { get; } = new();
    public string? Error { get; private set; }
    public event Action? Changed;

    public Discovery()
    {
        _purge.Tick += (_, _) => Purge();
    }

    public void Start()
    {
        Stop();
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.ExclusiveAddressUse = false;
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.EnableBroadcast = true;
            s.Bind(new IPEndPoint(IPAddress.Any, Port));
            _sock = s;
            Error = null;
        }
        catch (Exception ex)
        {
            Error = $"Can't listen on UDP {Port} ({ex.Message}). Use the manual IP box.";
            Changed?.Invoke();
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var sock = _sock;
        _ = Task.Run(() => Loop(sock, ct));
        _purge.Start();
        Changed?.Invoke();
    }

    public void Rescan()
    {
        Radios.Clear();
        Start();
    }

    async Task Loop(Socket sock, CancellationToken ct)
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await sock.ReceiveFromAsync(buf.AsMemory(), SocketFlags.None, any, ct);
                var from = ((IPEndPoint)r.RemoteEndPoint).Address.ToString();
                var kv = ParsePacket(buf, r.ReceivedBytes);
                if (kv != null) Ui.Post(() => Upsert(kv, from));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    /// <summary>Discovery payload is ASCII key=value text after a 28-byte VITA-49 header.</summary>
    internal static Dictionary<string, string>? ParsePacket(byte[] buf, int len)
    {
        string text = len > 28 ? Encoding.ASCII.GetString(buf, 28, len - 28) : "";
        if (!text.Contains("model=")) text = Encoding.ASCII.GetString(buf, 0, len);
        if (!text.Contains("model=")) return null;

        // 0x7F encodes a space inside values; keep it out of the tokenizer's way.
        var chars = text.Replace('\u007f', '\u00a0').ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] < 32) chars[i] = ' ';
        return Kv.Parse(Kv.Tokenize(new string(chars)));
    }

    void Upsert(Dictionary<string, string> kv, string from)
    {
        string serial = kv.GetValueOrDefault("serial") ?? from;
        var r = Radios.FirstOrDefault(x => x.Serial == serial);
        if (r == null)
        {
            r = new RadioInfo { Serial = serial };
            Radios.Add(r);
            Changed?.Invoke();
        }
        r.Model = kv.GetValueOrDefault("model") ?? r.Model;
        r.Nickname = kv.GetValueOrDefault("nickname") ?? kv.GetValueOrDefault("name") ?? r.Nickname;
        r.Callsign = kv.GetValueOrDefault("callsign") ?? r.Callsign;
        r.Ip = kv.GetValueOrDefault("ip") ?? from;
        if (int.TryParse(kv.GetValueOrDefault("port"), out var p) && p > 0) r.Port = p;
        r.Version = kv.GetValueOrDefault("version") ?? r.Version;
        r.Status = kv.GetValueOrDefault("status") ?? r.Status;
        r.Stations = (kv.GetValueOrDefault("gui_client_stations") ?? "").Replace(',', ' ').Trim();
        r.LastSeen = DateTime.UtcNow;
    }

    void Purge()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-15);
        foreach (var r in Radios.Where(x => x.LastSeen < cutoff).ToList()) Radios.Remove(r);
        Changed?.Invoke();
    }

    public void Stop()
    {
        _purge.Stop();
        try { _cts?.Cancel(); } catch { }
        try { _sock?.Close(); } catch { }
        _sock = null;
        _cts = null;
    }

    public void Dispose() => Stop();
}
