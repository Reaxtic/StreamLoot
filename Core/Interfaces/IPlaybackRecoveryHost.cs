namespace Core.Interfaces;

/// <summary>Rebuilds a failed player controller without clearing its persisted login profile.</summary>
public interface IPlaybackRecoveryHost
{
    Task RecreatePlayerAsync(string channelUrl);
}
