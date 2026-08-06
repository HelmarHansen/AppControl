using System.Text.Json;
using System.Text.Json.Serialization;
using AppControl.Host.Core;

namespace AppControl.Host.Net;

/// <summary>
/// Nachrichten des DataChannels `ac-control`. Siehe docs/04-protocol.md §4.4.
///
/// Diese Nachrichten sind die EINZIGE Quelle fuer das, was der Viewer anzeigt.
/// Der Viewer leitet nichts aus dem Videostrom ab: Ein stehendes Bild wegen
/// Netzproblemen sieht sonst identisch aus wie eine bewusste Pause des Hosts -
/// und der Unterschied ist genau das, worum es bei Transparenz geht.
/// </summary>
public static class ControlMessages
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed record ScopeInfo
    {
        [JsonPropertyName("kind")]   public string Kind { get; init; } = "screen";  // "window" | "screen"
        [JsonPropertyName("title")]  public string Title { get; init; } = "";
        [JsonPropertyName("width")]  public int Width { get; init; }
        [JsonPropertyName("height")] public int Height { get; init; }

        public static ScopeInfo From(ShareScope scope)
        {
            var rect = scope.GetTargetRect();
            return new ScopeInfo
            {
                Kind = scope is ShareScope.SingleWindow ? "window" : "screen",
                Title = scope.DisplayName,
                Width = rect.Width,
                Height = rect.Height,
            };
        }
    }

    public sealed record ShareState
    {
        [JsonPropertyName("t")]              public string Type => "share-state";
        [JsonPropertyName("state")]          public string State { get; init; } = "stopped";
        [JsonPropertyName("scope")]          public ScopeInfo? Scope { get; init; }
        [JsonPropertyName("controlGranted")] public bool ControlGranted { get; init; }
        [JsonPropertyName("elapsedSec")]     public long ElapsedSec { get; init; }
        [JsonPropertyName("remainingSec")]   public long? RemainingSec { get; init; }

        public static ShareState From(SessionSnapshot snapshot) => new()
        {
            State = snapshot.State switch
            {
                SessionState.Sharing => "sharing",
                SessionState.Paused => "paused",
                _ => "stopped",
            },
            Scope = snapshot.Scope is null ? null : ScopeInfo.From(snapshot.Scope),
            ControlGranted = snapshot.ControlGranted,
            ElapsedSec = (long)snapshot.Elapsed.TotalSeconds,
            RemainingSec = snapshot.Remaining is { } r ? (long)r.TotalSeconds : null,
        };
    }

    public sealed record ControlState
    {
        [JsonPropertyName("t")]       public string Type => "control-state";
        [JsonPropertyName("granted")] public bool Granted { get; init; }
        [JsonPropertyName("reason")]  public string Reason { get; init; } = "";
    }

    public sealed record InputRejected
    {
        [JsonPropertyName("t")]      public string Type => "input-rejected";
        [JsonPropertyName("gate")]   public int Gate { get; init; }
        [JsonPropertyName("reason")] public string Reason { get; init; } = "";
        [JsonPropertyName("count")]  public long Count { get; init; }
    }

    public sealed record Stats
    {
        [JsonPropertyName("t")]           public string Type => "stats";
        [JsonPropertyName("fps")]         public double Fps { get; init; }
        [JsonPropertyName("bitrateKbps")] public int BitrateKbps { get; init; }
        [JsonPropertyName("rttMs")]       public double RttMs { get; init; }
        [JsonPropertyName("encodeMs")]    public double EncodeMs { get; init; }
        [JsonPropertyName("packetsLost")] public long PacketsLost { get; init; }
    }

    public sealed record Bye
    {
        [JsonPropertyName("t")]      public string Type => "bye";
        [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    }

    /// <summary>Uebersetzt ein Gate-Ergebnis in eine Meldung, die der Viewer anzeigen kann.</summary>
    public static InputRejected FromRejection(Input.GateRejection rejection, long count) => rejection switch
    {
        Input.GateRejection.NotSharing =>
            new InputRejected { Gate = 1, Reason = "session-not-sharing", Count = count },
        Input.GateRejection.ControlNotGranted =>
            new InputRejected { Gate = 2, Reason = "control-not-granted", Count = count },
        Input.GateRejection.WindowNotForeground =>
            new InputRejected { Gate = 3, Reason = "target-window-not-foreground", Count = count },
        Input.GateRejection.TargetRectInvalid =>
            new InputRejected { Gate = 4, Reason = "target-rect-invalid", Count = count },
        Input.GateRejection.KeyBlocked =>
            new InputRejected { Gate = 5, Reason = "key-blocked-by-policy", Count = count },
        _ => new InputRejected { Gate = 0, Reason = "unknown", Count = count },
    };
}
