using Core.Models;

namespace Core.Services;

public static class CampaignAvailabilityPolicy
{
    public static (CampaignAvailability State, int Online) Resolve(CampaignAvailability state, int online, bool confirmedLive)
        => confirmedLive ? (CampaignAvailability.Available, Math.Max(1, online)) : (state, online);

    public static (CampaignAvailability State, int Online) Kick(IReadOnlyList<string> channels, IReadOnlyDictionary<string, int> statuses)
    {
        int online = channels.Count(ch => statuses.TryGetValue(ch, out int value) && value >= 0);
        if (online > 0) return (CampaignAvailability.Available, online);
        bool unknown = channels.Any(ch => !statuses.TryGetValue(ch, out int value) || value < -1);
        return (unknown ? CampaignAvailability.Unknown : CampaignAvailability.Unavailable, 0);
    }
}
