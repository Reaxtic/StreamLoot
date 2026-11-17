using System.Text.Json;
using System.Text.Json.Nodes;

namespace Core.Services;

/// <summary>Missing Twitch data is a failed read, not an empty inventory.</summary>
public static class TwitchDashboardResponse
{
    public static JsonArray Parse(string json)
    {
        try
        {
            return Validate(JsonNode.Parse(json) as JsonArray
                ?? throw new InvalidOperationException("Twitch dashboard response was not a batch."));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Twitch dashboard response was not valid JSON.", ex);
        }
    }

    public static JsonArray Validate(JsonArray batch)
    {
        if (batch.Count < 2 || batch[0] is not JsonObject inventory || batch[1] is not JsonObject dashboard)
            throw new InvalidOperationException("Twitch dashboard response was incomplete.");
        if (HasErrors(inventory) || HasErrors(dashboard))
            throw new InvalidOperationException("Twitch dashboard returned GraphQL errors.");
        if (dashboard["data"]?["currentUser"] is not JsonObject user ||
            string.IsNullOrWhiteSpace(user["id"]?.GetValue<string>()) ||
            user["dropCampaigns"] is not JsonArray)
            throw new InvalidOperationException("Twitch did not return a campaign list (missing/null dropCampaigns or user).");
        if (inventory["data"]?["currentUser"] is not JsonObject inventoryUser ||
            inventoryUser["inventory"]?["dropCampaignsInProgress"] is not JsonArray)
            throw new InvalidOperationException("Twitch did not return the drops inventory.");
        if (inventoryUser["id"]?.GetValue<string>() != user["id"]?.GetValue<string>())
            throw new InvalidOperationException("Twitch dashboard and inventory belong to different sessions.");
        return batch;
    }

    public static bool HasErrors(JsonObject response) => response["errors"] is JsonArray errors && errors.Count > 0;
}
