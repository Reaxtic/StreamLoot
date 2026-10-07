namespace Core.Managers;

/// <summary>Transport failures are not evidence that a campaign cannot earn drops.</summary>
public sealed class TwitchConnectionRecoveryPolicy
{
    public static readonly TimeSpan RecoveryGrace = TimeSpan.FromMinutes(3);
    private readonly object _sync = new();
    private readonly Dictionary<string, (bool Interrupted, DateTime ObserveAfter)> _states = new();
    private readonly Dictionary<string, int> _heartbeatFailures = new();

    // A single lost analytics event must not repeatedly reset an otherwise healthy observation window.
    public bool RecordHeartbeatFailure(string campaignId)
    {
        lock (_sync)
        {
            int failures = _heartbeatFailures.GetValueOrDefault(campaignId) + 1;
            _heartbeatFailures[campaignId] = failures;
            return failures >= 2;
        }
    }

    public bool RecordFailure(string campaignId)
    {
        lock (_sync)
        {
            bool first = !_states.TryGetValue(campaignId, out var previous) || !previous.Interrupted;
            _states[campaignId] = (true, DateTime.MaxValue);
            return first;
        }
    }

    public bool RecordSuccess(string campaignId, DateTime now)
    {
        lock (_sync)
        {
            _heartbeatFailures.Remove(campaignId);
            if (!_states.TryGetValue(campaignId, out var previous) || !previous.Interrupted) return false;
            _states[campaignId] = (false, now + RecoveryGrace);
            return true;
        }
    }

    public bool CanObserveStall(string campaignId, DateTime now)
    {
        lock (_sync)
            return !_states.TryGetValue(campaignId, out var state)
                || (!state.Interrupted && now >= state.ObserveAfter);
    }
}
