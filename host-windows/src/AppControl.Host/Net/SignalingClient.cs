using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Net;

public sealed record IceServerConfig
{
    [JsonPropertyName("urls")]       public string[] Urls { get; init; } = [];
    [JsonPropertyName("username")]   public string? Username { get; init; }
    [JsonPropertyName("credential")] public string? Credential { get; init; }
}

/// <summary>
/// WebSocket-Client zum Rendezvous-Server.
///
/// Der Server sieht ausschliesslich die Raum-ID und undurchsichtige Base64-Bloecke.
/// Diese Klasse kennt daher keinerlei Anwendungslogik - sie transportiert Bytes.
/// Die Verschluesselung passiert eine Schicht darueber in SessionController.
/// Siehe docs/04-protocol.md §4.1.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly ILogger<SignalingClient> _log;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    /// <summary>Ein verschluesselter Umschlag von der Gegenseite (bereits Base64-dekodiert).</summary>
    public event Action<byte[]>? EnvelopeReceived;

    public event Action<IceServerConfig[]>? IceServersReceived;
    public event Action? PeerJoined;
    public event Action? PeerLeft;
    public event Action<string, string>? ServerError;
    public event Action<Exception?>? Disconnected;

    public SignalingClient(ILogger<SignalingClient> log) => _log = log;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri serverUri, string roomId, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        await _socket.ConnectAsync(serverUri, _cts.Token);
        _log.LogInformation("Mit Signaling-Server verbunden: {Uri}", serverUri);

        await SendJsonAsync(new { t = "join", roomId, role = "host", v = "ac/1" }, _cts.Token);

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>Sendet einen verschluesselten Umschlag an die Gegenseite.</summary>
    public Task SendEnvelopeAsync(ReadOnlyMemory<byte> envelope, CancellationToken ct = default)
        => SendJsonAsync(new { t = "relay", payload = Convert.ToBase64String(envelope.Span) }, ct);

    private async Task SendJsonAsync(object message, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("Signaling-Verbindung ist nicht offen");

        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        await _socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // 256 KiB: Ein SDP mit vielen ICE-Kandidaten kann mehrere zehn KiB gross
        // werden; der Server begrenzt bei 128 KiB Base64.
        var buffer = new byte[256 * 1024];
        Exception? failure = null;

        try
        {
            while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                var received = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer, received, buffer.Length - received), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Signaling-Server hat die Verbindung geschlossen: {Status} {Desc}",
                            result.CloseStatus, result.CloseStatusDescription);
                        return;
                    }
                    received += result.Count;
                }
                while (!result.EndOfMessage && received < buffer.Length);

                HandleMessage(Encoding.UTF8.GetString(buffer, 0, received));
            }
        }
        catch (OperationCanceledException) { /* normaler Abbau */ }
        catch (Exception ex)
        {
            failure = ex;
            _log.LogError(ex, "Signaling-Verbindung abgebrochen");
        }
        finally
        {
            Disconnected?.Invoke(failure);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var typeProp)) return;

            switch (typeProp.GetString())
            {
                case "relay":
                    if (root.TryGetProperty("payload", out var payload))
                    {
                        var raw = Convert.FromBase64String(payload.GetString() ?? "");
                        EnvelopeReceived?.Invoke(raw);
                    }
                    break;

                case "joined":
                    _log.LogInformation("Raum betreten, Gegenpart anwesend: {Present}",
                        root.TryGetProperty("peerPresent", out var p) && p.GetBoolean());
                    if (root.TryGetProperty("peerPresent", out var pp) && pp.GetBoolean())
                        PeerJoined?.Invoke();
                    break;

                case "peer-joined": PeerJoined?.Invoke(); break;
                case "peer-left":   PeerLeft?.Invoke();   break;

                case "ice-servers":
                    if (root.TryGetProperty("servers", out var servers))
                    {
                        var parsed = servers.Deserialize<IceServerConfig[]>() ?? [];
                        _log.LogInformation("{Count} ICE-Server erhalten", parsed.Length);
                        IceServersReceived?.Invoke(parsed);
                    }
                    break;

                case "error":
                    var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "?" : "?";
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    _log.LogWarning("Signaling-Fehler {Code}: {Message}", code, msg);
                    ServerError?.Invoke(code, msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Kaputtes JSON vom Server ist kein Grund abzustuerzen - aber ein
            // Grund, es sichtbar zu machen.
            _log.LogWarning(ex, "Unlesbare Nachricht vom Signaling-Server verworfen");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket is { State: WebSocketState.Open })
            {
                await SendJsonAsync(new { t = "leave" }, CancellationToken.None);
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch { /* beim Abbau ist ein Fehler folgenlos */ }

        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _socket?.Dispose();
        _cts?.Dispose();
    }
}
