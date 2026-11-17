namespace Core.Services;

public enum PlaybackRepair { None, Reload, Recreate }

/// <summary>Per-player escalation, independent of campaign crediting and network availability.</summary>
public sealed class PlaybackRecoveryState
{
    private string? _channel;
    private DateTime _unhealthySince;
    private DateTime _lastRepair;
    private DateTime _lastAdvance;
    private double? _position;
    private int _attempts;
    public bool Healthy { get; private set; }

    public void Reset()
    {
        _channel = null;
        _unhealthySince = _lastRepair = _lastAdvance = DateTime.MinValue;
        _position = null;
        _attempts = 0;
        Healthy = false;
    }

    public PlaybackRepair Observe(TwitchPlaybackSnapshot? state, string channel, string host, DateTime now)
    {
        if (_channel != channel) { Reset(); _channel = channel; }
        bool correctPage = state != null && Uri.TryCreate(state.Url, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && (uri.Host == host || uri.Host == "www." + host)
            && uri.AbsolutePath.Trim('/').Equals(channel, StringComparison.OrdinalIgnoreCase);
        bool advancing = state?.CurrentTime is double position &&
            (!_position.HasValue || Math.Abs(position - _position.Value) > 0.1);
        if (advancing) { _lastAdvance = now; _position = state!.CurrentTime; }
        // Metadata alone cannot distinguish an actively playing video from a frozen renderer.
        bool frozen = state?.CurrentTime != null && _lastAdvance != DateTime.MinValue
            && now - _lastAdvance >= TimeSpan.FromMinutes(1);
        Healthy = correctPage && state!.IsPlaying && !frozen;
        if (Healthy)
        {
            _unhealthySince = DateTime.MinValue;
            // A single readyState=4 after reload is not proof of sustained recovery.
            if (_lastRepair != DateTime.MinValue && now - _lastRepair >= TimeSpan.FromMinutes(2)) _attempts = 0;
            return PlaybackRepair.None;
        }
        if (_unhealthySince == DateTime.MinValue) _unhealthySince = now;
        bool wrongPage = state != null && !correctPage;
        bool loadedPause = correctPage && state!.HasVideo && state.Paused && state.ReadyState >= 2;
        if ((!wrongPage && !frozen && !loadedPause && now - _unhealthySince < TimeSpan.FromMinutes(1)) ||
            (_lastRepair != DateTime.MinValue && now - _lastRepair < TimeSpan.FromMinutes(2))) return PlaybackRepair.None;
        _lastRepair = now;
        _position = null;
        _lastAdvance = DateTime.MinValue;
        return ++_attempts <= 2 ? PlaybackRepair.Reload : PlaybackRepair.Recreate;
    }
}
