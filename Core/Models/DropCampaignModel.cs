using Core.Enums;

namespace Core.Models
{
    /// <summary>A single drop/reward shown in the Dashboard "also earning" list, with its own progress.</summary>
    public sealed record CoMiningDrop(string Name, string? ImageUrl, int Percent);

    /// <summary>A pickable channel for the current campaign (used by the Dashboard channel list).</summary>
    public sealed record ChannelCandidate(string Login, string Url, bool Online, int Viewers = 0);

    /// <summary>Whether a campaign currently has streamers you can watch to earn it.</summary>
    public enum CampaignAvailability
    {
        /// <summary>Not yet checked.</summary>
        Unknown,
        /// <summary>At least one of the campaign's listed channels is live now.</summary>
        Available,
        /// <summary>None of the campaign's listed channels are live right now.</summary>
        Unavailable,
        /// <summary>General/category drop — earnable on any live channel of the game, so effectively always available.</summary>
        Category
    }

    /// <summary>
    /// Lightweight view of a campaign that is progressing alongside the watched one on the same channel
    /// (e.g. a general drop earned simultaneously). Broken down into its individual drops so each shows
    /// its own percentage, like the Kick inventory. Used for the Dashboard "also earning" list.
    /// </summary>
    public sealed record CoMiningCampaign(string Name, bool IsGeneralDrop, IReadOnlyList<CoMiningDrop> Drops);

    /// <summary>
    /// Represents a reward available through a drops campaign, including progress and claim status information.
    /// </summary>
    /// <param name="Id">The unique identifier for the reward.</param>
    /// <param name="Name">The display name of the reward.</param>
    /// <param name="ImageUrl">The URL of the image representing the reward, or null if no image is available.</param>
    /// <param name="RequiredMinutes">The total number of minutes required to earn the reward.</param>
    /// <param name="ProgressMinutes">The number of minutes of progress accumulated toward earning the reward API Based. Defaults to 0.</param>
    /// <param name="ProgressMinutes">The number of minutes of progress accumulated toward earning the reward Mutable for display. Defaults to 0.</param>
    /// <param name="IsClaimed">true if the reward has been claimed; otherwise, false. Defaults to false.</param>
    /// <param name="DropInstanceId">The identifier of the specific drop instance associated with this reward, or null if not applicable.</param>
    /// <param name="IsCurrentReward">true if this reward is currently being progressed; otherwise, false. Defaults to false.</param>
    public record DropsReward(
        string Id,
        string Name,
        string? ImageUrl,
        int RequiredMinutes,
        int ProgressMinutes = 0,
        bool IsClaimed = false,
        string? DropInstanceId = null,
        bool IsCurrentReward = false)
    {
        /// <summary>Completion percentage (0–100) of this reward, derived from progress vs. required minutes.</summary>
        public int ProgressPercent => RequiredMinutes <= 0
            ? 0
            : (int)Math.Min(100, Math.Round(ProgressMinutes * 100.0 / RequiredMinutes));
    }
    /// <summary>
    /// Represents a campaign that offers in-game rewards through a drops program for a specific game and platform.
    /// </summary>
    /// <param name="Id">The unique identifier for the drops campaign.</param>
    /// <param name="Name">The display name of the drops campaign.</param>
    /// <param name="Slug">A URL-friendly identifier for the campaign, often used in API endpoints or web URLs.</param>
    /// <param name="GameName">The name of the game associated with the campaign.</param>
    /// <param name="GameImageUrl">The URL of the image representing the game. Can be null if no image is available.</param>
    /// <param name="StartsAt">The date and time when the campaign becomes active, in UTC.</param>
    /// <param name="EndsAt">The date and time when the campaign ends, in UTC.</param>
    /// <param name="Rewards">A read-only list of rewards available in this campaign. Cannot be null or empty.</param>
    /// <param name="Platform">The platform on which the campaign is available.</param>
    /// <param name="ConnectUrls">A read-only list of URLs that users can use to connect their accounts for eligibility. Cannot be null.</param>
    /// <param name="IsCurrentCampaign">true if this campaign is currently being watched; otherwise, false. Defaults to false.</param>
    public record DropsCampaign(
        string Id,
        string Name,
        string Slug,
        string GameName,
        string? GameImageUrl,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        IReadOnlyList<DropsReward> Rewards,
        Platform Platform,
        IReadOnlyList<string> ConnectUrls,
        bool IsGeneralDrop,
        bool IsCurrentCampaign = false,
        CampaignAvailability Availability = CampaignAvailability.Unknown,
        int OnlineChannels = 0,
        bool IsPinned = false,
        bool IsStalled = false)
    {
        /// <summary>
        /// True when at least one reward is fully watched but still unclaimed — typically because the game account
        /// isn't linked, so the auto-claim keeps failing. The Inventory shows a "ready to claim / connect account"
        /// hint for these, and the miner does not keep watching them (no watch time left to earn).
        /// </summary>
        public bool HasClaimableUnclaimed =>
            Rewards.Any(r => !r.IsClaimed && r.RequiredMinutes > 0 && r.ProgressMinutes >= r.RequiredMinutes);

        /// <summary>Minutes of watching left until the next unclaimed drop (int.MaxValue when none).</summary>
        public int NextDropEtaMinutes =>
            Rewards.Where(r => !r.IsClaimed && r.ProgressMinutes < r.RequiredMinutes)
                   .Select(r => r.RequiredMinutes - r.ProgressMinutes)
                   .DefaultIfEmpty(int.MaxValue)
                   .Min();

        /// <summary>Human-readable time to the next drop ("⏱ ~1h 23m"); empty when nothing is left to watch.</summary>
        public string EtaText
        {
            get
            {
                int eta = NextDropEtaMinutes;
                if (eta == int.MaxValue)
                    return string.Empty;
                return eta >= 60 ? $"⏱ ~{eta / 60}h {eta % 60}m" : $"⏱ ~{eta}m";
            }
        }
    }
}
