using System.Text.Json.Nodes;

namespace Core.Services;

public sealed record TwitchPlaybackSnapshot(string Url, bool HasVideo, bool Paused, bool Ended, int ReadyState, double? CurrentTime = null, int? MediaError = null, int? NetworkState = null, bool HasSource = false)
{
    public bool IsPlaying => HasVideo && !Paused && !Ended && ReadyState >= 2;
}

/// <summary>Streamer availability is not proof that the local browser still has its player open.</summary>
public static class TwitchPlaybackPolicy
{
    public static bool IsSelectedChannelPage(string? url, string? login)
    {
        if (string.IsNullOrWhiteSpace(login) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Scheme == "https" && (uri.Host.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase)) &&
            uri.AbsolutePath.Trim('/').Equals(login.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static TwitchPlaybackSnapshot? Parse(string result)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(result);
            // WebView2 wraps the JSON string returned by ExecuteScriptAsync in another JSON string.
            if (node is JsonValue value && value.TryGetValue<string>(out string? text)) node = JsonNode.Parse(text);
            if (node is not JsonObject state) return null;
            return new(state["url"]?.GetValue<string>() ?? "", state["hasVideo"]?.GetValue<bool>() ?? false,
                state["paused"]?.GetValue<bool>() ?? true, state["ended"]?.GetValue<bool>() ?? false,
                state["readyState"]?.GetValue<int>() ?? 0, state["currentTime"]?.GetValue<double>(),
                state["mediaError"]?.GetValue<int>(), state["networkState"]?.GetValue<int>(), state["hasSource"]?.GetValue<bool>() ?? false);
        }
        catch { return null; }
    }

    public static bool CanObserveStall(TwitchPlaybackSnapshot? state, string? login)
        => state != null && IsSelectedChannelPage(state.Url, login) && state.IsPlaying;

    public static bool RepairDue(DateTime now, DateTime lastRepair)
        => now - lastRepair >= TimeSpan.FromMinutes(2);

    public static bool ShouldRecover(TwitchPlaybackSnapshot? state, string? login, DateTime unhealthySince,
        DateTime now, DateTime lastRepair)
        => state != null && RepairDue(now, lastRepair) &&
            (!IsSelectedChannelPage(state.Url, login) || (!state.IsPlaying && now - unhealthySince >= TimeSpan.FromMinutes(1)));
}
