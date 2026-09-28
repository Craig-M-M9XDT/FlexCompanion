using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FlexCompanion.Flex;

/// <summary>
/// Minimal client for AetherSDR's opt-in local AutomationServer.
/// It never opens a network port: Aether advertises a same-machine QLocalServer
/// through a JSON discovery file in the OS temp directory.
/// </summary>
public static class AetherAutomationClient
{
    public readonly record struct Result(bool Ok, string Message);

    public static async Task<Result> SetTxRn2Async(bool enabled, CancellationToken cancellationToken = default)
    {
        var discovery = FindDiscovery();
        if (discovery == null)
            return new(false, "Aether automation bridge not found. Enable Aether's local automation bridge first.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            await using var stream = await OpenStreamAsync(discovery.Value.Socket, timeout.Token);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await writer.WriteLineAsync($"invoke gateRn2 setChecked {(enabled ? "true" : "false")}");
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
                return new(false, "Aether automation bridge closed without a response.");

            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (ok)
            {
                var tag = string.IsNullOrWhiteSpace(discovery.Value.Label)
                    ? discovery.Value.Version
                    : discovery.Value.Label;
                return new(true, string.IsNullOrWhiteSpace(tag)
                    ? $"Aether TX RN2 {(enabled ? "enabled" : "disabled")}."
                    : $"Aether TX RN2 {(enabled ? "enabled" : "disabled")} ({tag}).");
            }

            var error = ReadText(root, "error") ?? ReadText(root, "message") ?? line;
            if (error.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("token", StringComparison.OrdinalIgnoreCase))
                return new(false, "Aether automation requires an authentication token. Token-enabled control is not configured in Companion yet.");
            if (error.Contains("not visible", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("hidden", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return new(false, "Aether TX RN2 control is not currently exposed by the automation UI. Open AetherTX → Gate, then try RN2 again.");

            return new(false, $"Aether RN2: {error}");
        }
        catch (OperationCanceledException)
        {
            return new(false, "Timed out waiting for Aether's automation bridge.");
        }
        catch (Exception ex)
        {
            return new(false, $"Aether automation unavailable: {ex.Message}");
        }
    }

    static string? ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    static async Task<Stream> OpenStreamAsync(string socketName, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = NormalizeWindowsPipe(socketName);
            var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(1800, cancellationToken);
            return stream;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketName), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static string NormalizeWindowsPipe(string value)
    {
        var s = value.Trim();
        const string marker = "\\pipe\\";
        var p = s.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (p >= 0) s = s[(p + marker.Length)..];
        s = s.TrimStart('\\', '/');
        if (s.Length == 0) throw new InvalidOperationException("Aether discovery contained an empty pipe name.");
        return s;
    }

    static Discovery? FindDiscovery()
    {
        try
        {
            var temp = Path.GetTempPath();
            var files = new List<FileInfo>();
            var legacy = Path.Combine(temp, "aethersdr-automation.json");
            if (File.Exists(legacy)) files.Add(new FileInfo(legacy));

            var dir = Path.Combine(temp, "aethersdr-automation");
            if (Directory.Exists(dir))
                files.AddRange(new DirectoryInfo(dir).EnumerateFiles("*.json"));

            foreach (var file in files.OrderByDescending(x => x.LastWriteTimeUtc))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file.FullName));
                    var root = json.RootElement;
                    var socket = ReadText(root, "socket") ?? ReadText(root, "name");
                    if (string.IsNullOrWhiteSpace(socket)) continue;
                    return new Discovery(socket,
                        ReadText(root, "label") ?? string.Empty,
                        ReadText(root, "version") ?? string.Empty);
                }
                catch
                {
                    // A stale/partial discovery file must not hide a newer valid instance.
                }
            }
        }
        catch
        {
            // Discovery is best-effort; callers get the normal unavailable message.
        }
        return null;
    }

    readonly record struct Discovery(string Socket, string Label, string Version);
}
