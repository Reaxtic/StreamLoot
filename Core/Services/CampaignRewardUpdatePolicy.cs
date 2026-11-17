using Core.Models;

namespace Core.Services;

/// <summary>Keep unchanged inventory rows, their images and their visual trees alive.</summary>
public static class CampaignRewardUpdatePolicy
{
    public static DropsCampaign Apply(DropsCampaign campaign, IReadOnlyList<DropsReward> rewards)
        => campaign.Rewards.SequenceEqual(rewards) ? campaign : campaign with { Rewards = rewards };
}
