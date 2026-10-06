namespace Core.Managers;

/// <summary>Transport failures are not evidence that a campaign cannot earn drops.</summary>
public sealed class TwitchConnectionRecoveryPolicy
{
    public static readonly TimeSpan RecoveryGrace = TimeSpan.FromMinutes(3);
    private readonly object _sync = new();
    private readonly Dictionary<string, (bool Interrupted, DateTime ObserveAfter)> _states = new();

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
