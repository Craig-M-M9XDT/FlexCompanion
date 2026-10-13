// APD LAB simulator — explicitly synthetic, localhost-only, NEVER controls RF hardware.
// Standalone SmartSDR text protocol + synthetic VITA meter stream for UI/integration testing.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

Console.Title = "APD LAB SIMULATOR — NO RF HARDWARE";
try
{
    await new ApdLoopbackServer().RunAsync();
}
catch (OperationCanceledException) { }
catch (SocketException ex)
{
    Console.Error.WriteLine($"APD simulator could not listen on 127.0.0.1:4992: {ex.Message}");
    Console.Error.WriteLine("Close the competing app on port 4992 and try again.");
    Environment.ExitCode = 1;
}

sealed class ApdLoopbackServer
{
    const int Port = 4992;
    readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    readonly CancellationTokenSource _stop = new();
    MockSession? _session;

    public async Task RunAsync()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stop.Cancel(); _listener.Stop(); };
        _listener.Start();
        Console.WriteLine("===============================================");
        Console.WriteLine(" FLEX APD LAB SIMULATOR — SYNTHETIC / NO RF");
        Console.WriteLine(" 127.0.0.1:4992 only; NOT a real FLEX-8600");
        Console.WriteLine("===============================================");
        Console.WriteLine("Start Flex Control Companion APD LAB; connect 127.0.0.1 or click");
        Console.WriteLine("'Start simulated FLEX-8600' on the APD tab.");
        Console.WriteLine("In this console: help | drive 65 | freq 14.150000 | ant ANT2");
        Console.WriteLine("                 tx on | tx off | reset | quit");
        _ = Task.Run(ConsoleLoop);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using (client)
                {
                    Console.WriteLine("Companion connected to synthetic radio.");
                    var session = new MockSession(client);
                    _session = session;
                    try { await session.RunAsync(_stop.Token); }
                    catch (OperationCanceledException) { }
                    catch (IOException) { }
                    finally { _session = null; Console.WriteLine("Companion disconnected."); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        finally { _listener.Stop(); }
    }

    void ConsoleLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            string? line = Console.ReadLine();
            if (line == null) return;
            string cmd = line.Trim();
            if (cmd.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                _stop.Cancel();
                _listener.Stop();
                return;
            }
            if (cmd.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("drive 0..100 (synthetic TX drive); freq <MHz>; ant ANT1/ANT2");
                Console.WriteLine("tx on/off (ONLY simulated meter values, no RF); reset; quit");
                continue;
            }
            var session = _session;
            if (session == null) { Console.WriteLine("Connect the APD LAB app first."); continue; }
            session.ConsoleCommand(cmd);
        }
    }
}

sealed class MockSession
{
    const string Handle = "0x12345678";
    readonly TcpClient _tcp;
    readonly SemaphoreSlim _wire = new(1, 1);
    readonly object _stateLock = new();
    readonly HashSet<ushort> _meterSubs = [];
    readonly UdpClient _udp = new();
    StreamWriter? _writer;
    int _udpPort;
    int _power = 50;
    string _antenna = "ANT1";
    string _samplerAnt1 = "INTERNAL", _samplerAnt2 = "INTERNAL";
    double _frequency = 14.100000;
    bool _apdEnabled, _eqActive, _mockTransmitting;
    int _sampleIndex = 1;
    DateTime _calSinceUtc = DateTime.UtcNow;
    readonly CultureInfo _ci = CultureInfo.InvariantCulture;

    public MockSession(TcpClient tcp) => _tcp = tcp;

    async Task SendAsync(string line)
    {
        await _wire.WaitAsync();
        try
        {
            if (_writer == null) return;
            await _writer.WriteLineAsync(line);
            await _writer.FlushAsync();
        }
        catch (IOException) { }
        finally { _wire.Release(); }
    }
    Task StatusAsync(string body) => SendAsync($"S{Handle}|{body}");
    Task ReplyAsync(string sequence, string body = "", string error = "0")
        => SendAsync($"R{sequence}|{error}|{body}");

    static readonly string MeterMetadata = string.Join("#", new[]
    {
        "1.src=SLC","1.num=0","1.nam=LEVEL","1.unit=dBm",
        "2.src=TX","2.num=0","2.nam=FWDPWR","2.unit=dBm",
        "3.src=TX","3.num=0","3.nam=SWR","3.unit=swr",
        "4.src=TX","4.num=0","4.nam=MICPEAK","4.unit=dB",
        "5.src=TX","5.num=0","5.nam=COMPPEAK","5.unit=dB",
        "6.src=TX","6.num=0","6.nam=ALC","6.unit=dBFS"
    });

    public async Task RunAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var ct = linked.Token;
        _tcp.NoDelay = true;
        using var ns = _tcp.GetStream();
        _writer = new StreamWriter(ns, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(ns, Encoding.ASCII, leaveOpen: true);
        await SendAsync("V4.2.20.41343");
        await SendAsync($"H{Handle}");

        var ticker = Task.Run(() => TickAsync(ct), ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith('C')) continue;
                var bar = line.IndexOf('|');
                if (bar < 2) continue;
                string seq = line[1..bar];
                string command = line[(bar + 1)..].Trim();
                await HandleCommandAsync(seq, command);
            }
        }
        finally
        {
            linked.Cancel();
            try { await ticker; } catch (OperationCanceledException) { }
            _udp.Dispose();
        }
    }

    async Task HandleCommandAsync(string seq, string command)
    {
        string lower = command.ToLowerInvariant();
        Console.WriteLine($"C{seq}: {command}");

        if (lower == "info")
        {
            await ReplyAsync(seq, "model=FLEX-8600 nickname=APD_SIMULATOR_NO_RF serial=SIMULATION_ONLY");
            return;
        }
        if (lower == "meter list")
        {
            await ReplyAsync(seq, MeterMetadata);
            return;
        }
        if (lower.StartsWith("client udpport "))
        {
            if (!int.TryParse(command["client udpport ".Length..], out int port) || port < 1 || port > 65535)
            { await ReplyAsync(seq, error: "50000004"); return; }
            _udpPort = port;
            await ReplyAsync(seq);
            return;
        }
        if (lower.StartsWith("sub meter "))
        {
            lock (_stateLock)
            {
                if (lower.EndsWith("all")) foreach (ushort m in Enumerable.Range(1,6).Select(x=>(ushort)x)) _meterSubs.Add(m);
                else if (ushort.TryParse(command[10..], out ushort meter)) _meterSubs.Add(meter);
            }
            await ReplyAsync(seq);
            return;
        }
        if (lower.StartsWith("unsub meter "))
        {
            lock (_stateLock)
            {
                if (lower.EndsWith("all")) _meterSubs.Clear();
                else if (ushort.TryParse(command[12..], out ushort meter)) _meterSubs.Remove(meter);
            }
            await ReplyAsync(seq);
            return;
        }
        if (lower.StartsWith("sub ") || lower.StartsWith("client program ") || lower.StartsWith("client bind "))
        {
            await ReplyAsync(seq);
            if (lower.StartsWith("sub slice")) await SendSliceAsync();
            else if (lower.StartsWith("sub tx")) await SendTxAsync();
            else if (lower.StartsWith("sub apd")) await SendApdAsync(includeSamplers: true);
            else if (lower.StartsWith("sub client")) await StatusAsync($"client {Handle} connected client_id=SIM0001 name=APD_SIMULATOR station=APD_SIM");
            return;
        }
        if (lower.StartsWith("apd enable="))
        {
            string value = command["apd enable=".Length..].Trim();
            if (value is not ("1" or "0")) { await ReplyAsync(seq, error: "50000004"); return; }
            lock (_stateLock)
            {
                _apdEnabled = value == "1";
                _eqActive = false;
                _calSinceUtc = DateTime.UtcNow;
            }
            await ReplyAsync(seq);
            await SendApdAsync();
            return;
        }
        if (lower == "apd reset")
        {
            lock (_stateLock) { _eqActive = false; _calSinceUtc = DateTime.UtcNow; ++_sampleIndex; }
            await ReplyAsync(seq);
            await StatusAsync("apd equalizer_reset");
            await SendApdAsync();
            return;
        }
        if (lower.StartsWith("apd sampler "))
        {
            var kv = command["apd sampler ".Length..].Split(' ',StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=',2)).Where(x=>x.Length==2)
                .ToDictionary(x=>x[0].ToLowerInvariant(),x=>x[1].ToUpperInvariant());
            string ant = kv.GetValueOrDefault("tx_ant") ?? "";
            string port = kv.GetValueOrDefault("sample_port") ?? "";
            var valid = ant == "ANT1" ? new [] { "INTERNAL","RX_A","XVTA" }
                      : ant == "ANT2" ? new [] { "INTERNAL","RX_B","XVTB" }
                      : Array.Empty<string>();
            if (!valid.Contains(port)) { await ReplyAsync(seq, error:"5000002D"); return; }
            lock(_stateLock) { if (ant == "ANT1") _samplerAnt1 = port; else _samplerAnt2 = port; _eqActive=false; _calSinceUtc=DateTime.UtcNow; }
            await ReplyAsync(seq);
            await SendApdAsync(includeSamplers:true);
            return;
        }
        if (lower.StartsWith("transmit set rfpower="))
        {
            if (!int.TryParse(command["transmit set rfpower=".Length..], out int value) || value is < 0 or > 100)
            { await ReplyAsync(seq,error:"50000004");return; }
            ChangeDrive(value);
            await ReplyAsync(seq);
            await SendTxAsync();
            await SendApdAsync();
            return;
        }
        // Never simulate accepting an actual TX key, unsafe write or an Alpha diagnostic download.
        // This simulator cannot transmit and deliberately has no physical radio path.
        await ReplyAsync(seq,error:"5000002D");
    }

    void Recalibrate() { _calSinceUtc = DateTime.UtcNow; _eqActive = false; ++_sampleIndex; }

    void ChangeDrive(int value)
    {
        lock (_stateLock) { if (_power != value) { _power = value; Recalibrate(); } }
    }
    public void ConsoleCommand(string cmd)
    {
        string[] parts=cmd.Split(' ',2,StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(parts.Length==0)return;
        string arg=parts.Length>1?parts[1]:"";
        bool ok=true;
        lock (_stateLock)
        {
            switch(parts[0].ToLowerInvariant())
            {
                case "tx": if (arg is "on" or "off") _mockTransmitting=arg=="on"; else ok=false;break;
                case "drive": if(int.TryParse(arg,out var drive) && drive is >=0 and <=100) {if(_power!=drive){_power=drive;Recalibrate();}}else ok=false;break;
                case "freq": if(double.TryParse(arg,NumberStyles.Float,_ci,out var f)&&f is >=1 and <=54) {_frequency=f;Recalibrate();}else ok=false;break;
                case "ant": if(arg is "ANT1" or "ANT2"){_antenna=arg;Recalibrate();}else ok=false;break;
                case "reset": Recalibrate();break;
                default:ok=false;break;
            }
        }
        Console.WriteLine(ok?"Synthetic state changed; awaiting next status tick. NO RF transmitted.":"Invalid command. Type help.");
    }

    async Task SendSliceAsync()
    {
        string ant; double mhz;
        lock(_stateLock){ant=_antenna;mhz=_frequency;}
        await StatusAsync($"slice 0 in_use=1 client_handle={Handle} index_letter=A RF_frequency={mhz.ToString("0.000000",_ci)} txant={ant} rxant=ANT1 mode=USB active=1 tx=1 wide=1 pan=0x40000000");
    }
    async Task SendTxAsync()
    {
        int power;lock(_stateLock)power=_power;
        await StatusAsync($"transmit rfpower={power} tunepower=10 max_power_level=100");
    }
    async Task SendApdAsync(bool includeSamplers=false)
    {
        bool enabled,active;int power,index;string ant,p1,p2;double freq;
        lock(_stateLock)
        {enabled=_apdEnabled;active=_eqActive;power=_power;index=_sampleIndex;ant=_antenna;p1=_samplerAnt1;p2=_samplerAnt2;freq=_frequency;}
        await StatusAsync($"apd enable={(enabled?1:0)} configurable=1");
        await StatusAsync($"apd slice=0 mmx=0 client_handle={Handle} ant={ant} freq={freq.ToString("0.000000",_ci)} rfpower={power} equalizer_active={(enabled&&active?1:0)} sample_index={index} tx_error_mHz=0.250000 rx_error_mHz=0.125000");
        if(includeSamplers)
        {
            await StatusAsync($"apd sampler tx_ant=ANT1 selected_sampler={p1} valid_samplers=RX_A,XVTA");
            await StatusAsync($"apd sampler tx_ant=ANT2 selected_sampler={p2} valid_samplers=RX_B,XVTB");
        }
    }

    async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        int ticks=0;
        while(await timer.WaitForNextTickAsync(ct))
        {
            bool changed=false;
            lock(_stateLock)
            {
                if(_apdEnabled && !_eqActive && DateTime.UtcNow - _calSinceUtc > TimeSpan.FromSeconds(3))
                {_eqActive=true;changed=true;}
            }
            if(changed||++ticks%4==0)
            {
                await SendSliceAsync();
                await SendTxAsync();
                await SendApdAsync();
            }
            if(_udpPort>0) await SendMeterPacketAsync();
        }
    }

    async Task SendMeterPacketAsync()
    {
        ushort[] ids;bool isTx;
        lock(_stateLock){ids=_meterSubs.OrderBy(x=>x).ToArray();isTx=_mockTransmitting;}
        if(ids.Length==0)return;
        byte[] packet=new byte[28+ids.Length*4];
        packet[0]=0x18; // VITA class-id present; no trailer
        int words=packet.Length/4;
        packet[2]=(byte)(words>>8);packet[3]=(byte)words;
        packet[14]=0x80;packet[15]=0x02; // FLEX VITA-49 meter class
        for(int i=0;i<ids.Length;i++)
        {
            ushort id=ids[i]; short raw=id switch
            {
                1 => (short)(-90*128),    // synthetic receive dBm
                2 => (short)((isTx?50:-80)*128), // synthetic 100W or no TX
                3 => (short)(1.2*128),   // synthetic SWR
                4 => (short)(-20*128),   // synthetic MIC
                5 => (short)(8*128),     // synthetic COMP
                6 => (short)(-12*128),   // synthetic ALC
                _ => (short)0
            };
            int off=28+i*4;
            packet[off]=(byte)(id>>8);packet[off+1]=(byte)id;
            packet[off+2]=(byte)(raw>>8);packet[off+3]=(byte)raw;
        }
        try { await _udp.SendAsync(packet,new IPEndPoint(IPAddress.Loopback,_udpPort)); }
        catch(SocketException) { }
    }
}
