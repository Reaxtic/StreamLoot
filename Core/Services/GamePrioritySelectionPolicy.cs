using Core.Models;

namespace Core.Services;

public static class GamePrioritySelectionPolicy
{
    public static async Task<DropsCampaign?> FindHigherAsync(DropsCampaign? current,
        IEnumerable<DropsCampaign> candidates, bool manualPinActive,
        Func<DropsCampaign, int> rank, Func<DropsCampaign, bool> allowed,
        Func<DropsCampaign, Task<bool>> available)
    {
        if (manualPinActive) return null;
        int currentRank = current == null ? int.MaxValue : rank(current);
        foreach (var candidate in candidates.Where(c => c.Id != current?.Id && allowed(c) && rank(c) < currentRank)
            .OrderBy(rank).ThenBy(c => c.NextDropEtaMinutes).ThenBy(c => c.EndsAt))
            if (await available(candidate)) return candidate;
        return null;
    }
}
