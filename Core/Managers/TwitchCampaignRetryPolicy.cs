namespace Core.Managers;

/// <summary>Campaign-scoped, temporary failures; never changes pins, preferences or claim state.</summary>
public sealed class TwitchCampaignRetryPolicy
{
    public const int ChannelAttemptLimit = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(6);
    private readonly object _sync = new();
    private readonly Dictionary<string, FailureState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _highestServerMinutes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string CampaignId, string RewardId), int> _highestRewardMinutes = new();

    private sealed class FailureState
    {
        public HashSet<string> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime? RetryAt { get; set; }
        public int Suspensions { get; set; }
    }

    private FailureState? Find(string campaignId, DateTime now)
    {
        if (!_states.TryGetValue(campaignId, out var state)) return null;
        if (state.RetryAt is { } retryAt && now >= retryAt)
        {
            state.RetryAt = null;
            state.Channels.Clear();
        }
        return state;
    }

    public bool IsSuspended(string campaignId, DateTime now)
    {
        lock (_sync) return Find(campaignId, now)?.RetryAt != null;
    }

    public bool IsChannelStalled(string campaignId, string login, DateTime now)
    {
        lock (_sync) return Find(campaignId, now)?.Channels.Contains(login) == true;
    }

    /// <returns>The retry deadline only when a new suspension starts.</returns>
    public DateTime? RecordStalledChannel(string campaignId, string? login, DateTime now)
    {
        lock (_sync)
        {
            var state = Find(campaignId, now);
            if (state == null) _states[campaignId] = state = new();
            if (state.RetryAt != null) return null;
            if (!string.IsNullOrWhiteSpace(login)) state.Channels.Add(login);
            if (!string.IsNullOrWhiteSpace(login) && state.Channels.Count < ChannelAttemptLimit) return null;
            return Suspend(state, now);
        }
    }

    public DateTime? SuspendIfExhausted(string campaignId, IReadOnlyCollection<string> candidates, DateTime now)
    {
        lock (_sync)
        {
            var state = Find(campaignId, now);
            if (state == null || state.RetryAt != null || candidates.Count == 0
                || !candidates.All(state.Channels.Contains)) return null;
            return Suspend(state, now);
        }
    }

    private static DateTime Suspend(FailureState state, DateTime now)
    {
        state.Suspensions = Math.Min(state.Suspensions + 1, 6);
        var delay = TimeSpan.FromMinutes(Math.Min(MaximumRetryDelay.TotalMinutes,
            RetryDelay.TotalMinutes * Math.Pow(2, state.Suspensions - 1)));
        return (state.RetryAt = now + delay).Value;
    }

    // Missing data (0) or reappearing old minutes are not new progress.
    public void ObserveServerMinutes(string campaignId, int minutes)
    {
        lock (_sync)
        {
            int previous = _highestServerMinutes.GetValueOrDefault(campaignId);
            if (minutes <= previous) return;
            _highestServerMinutes[campaignId] = minutes;
            _states.Remove(campaignId);
        }
    }

    public void ConfirmProgress(string campaignId)
    {
        lock (_sync) _states.Remove(campaignId);
    }

    public void ObserveServerRewardMinutes(string campaignId, string rewardId, int minutes)
    {
        lock (_sync)
        {
            var key = (campaignId, rewardId);
            if (minutes <= _highestRewardMinutes.GetValueOrDefault(key)) return;
            _highestRewardMinutes[key] = minutes;
            _states.Remove(campaignId);
        }
    }
}
