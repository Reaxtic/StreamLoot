namespace Core.Managers;

public static class TwitchPinResumePolicy
{
    public static bool ProbeDue(DateTime now, DateTime lastProbe)
        => now - lastProbe >= TimeSpan.FromMinutes(3);

    public static bool PinRequiresSelection(string? firstPin, string? currentCampaign,
        bool currentIsPinned, bool suspended, bool confirmedEarlierPin)
        => confirmedEarlierPin || (firstPin != null && !suspended && firstPin != currentCampaign && !currentIsPinned);

    public static bool ResumeAllowed(bool probeDue, bool campaignAllowed, bool hasLiveChannel)
        => probeDue && campaignAllowed && hasLiveChannel;
}
