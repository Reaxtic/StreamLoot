namespace Core.Managers;

public enum EngineRecoveryAction { None, RestartLoop, RestartProcess }

public static class EngineRecoveryPolicy
{
    public static EngineRecoveryAction Evaluate(TimeSpan sinceCompletedHeartbeat, bool paused)
    {
        if (sinceCompletedHeartbeat >= TimeSpan.FromMinutes(12)) return EngineRecoveryAction.RestartProcess;
        if (!paused && sinceCompletedHeartbeat >= TimeSpan.FromMinutes(6)) return EngineRecoveryAction.RestartLoop;
        return EngineRecoveryAction.None;
    }
}
