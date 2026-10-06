namespace Core.Services;

public static class TwitchClaimRecoveryPolicy
{
    public static bool ShouldProbe(bool trackedByServer, bool claimed, int minutes, int required, string? benefitType)
        => !trackedByServer && !claimed && required > 0;

    public static int ProbePriority(int minutes, int required, string? benefitType)
        => benefitType is "BADGE" or "EMOTE" ? 0
            : minutes > 0 && minutes >= required - 5 ? 1 : 2;

    public static bool IsConfirmedClaim(string? status)
        => status is "ELIGIBLE_FOR_ALL" or "DROP_INSTANCE_ALREADY_CLAIMED";
}
