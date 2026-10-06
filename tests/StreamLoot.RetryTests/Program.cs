using Core.Managers;
using Core.Services;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}

var now = new DateTime(2026, 10, 3, 16, 0, 0);
var policy = new TwitchCampaignRetryPolicy();
Check(policy.RecordStalledChannel("pinned", "first", now) == null, "first channel may rotate");
Check(policy.RecordStalledChannel("pinned", "FIRST", now) == null, "same channel is not another attempt");
Check(policy.RecordStalledChannel("pinned", "second", now) == null, "second channel may rotate");
var deadline = policy.RecordStalledChannel("pinned", "third", now);
Check(deadline == now.AddMinutes(15), "third distinct failure suspends for 15 minutes");
Check(policy.IsSuspended("pinned", now.AddMinutes(14)), "campaign stays suspended before deadline");
Check(!policy.IsSuspended("fallback", now), "other campaigns remain available");
Check(!policy.IsChannelStalled("fallback", "first", now), "same streamer is independent in another campaign");
Check(policy.RecordStalledChannel("pinned", "fourth", now.AddMinutes(5)) == null, "repeated checks cannot extend deadline");
Check(!policy.IsSuspended("pinned", now.AddMinutes(15)), "retry occurs at exact deadline");
Check(!policy.IsChannelStalled("pinned", "first", now.AddMinutes(15)), "retry clears failed channels");
Check(policy.RecordStalledChannel("pinned", "first", now.AddMinutes(15)) == null, "retry starts a fresh attempt budget");
policy.ConfirmProgress("pinned");
Check(!policy.IsChannelStalled("pinned", "first", now), "confirmed server progress clears failures");

policy.RecordStalledChannel("single", "only", now);
Check(policy.SuspendIfExhausted("single", Array.Empty<string>(), now) == null, "empty/offline directory is not proof of credit failure");
Check(policy.SuspendIfExhausted("single", new[] { "only", "fresh" }, now) == null, "untried live channel prevents early suspension");
Check(policy.SuspendIfExhausted("single", new[] { "ONLY" }, now) == now.AddMinutes(15), "one failed live channel cannot loop forever");
policy.ConfirmProgress("single");
Check(!policy.IsSuspended("single", now), "claimable reward or resumed progress can clear suspension");
Check(policy.RecordStalledChannel("unknown", null, now) == now.AddMinutes(15), "missing channel identity gets a bounded retry");
Console.WriteLine("All Twitch retry policy tests passed.");

var backoff = new TwitchCampaignRetryPolicy();
backoff.ObserveServerMinutes("stuck", 58);
var attemptAt = now;
foreach (int delay in new[] { 15, 30, 60, 120, 240, 360, 360 })
{
    Check(backoff.RecordStalledChannel("stuck", null, attemptAt) == attemptAt.AddMinutes(delay), $"backoff grows to {delay} minutes");
    backoff.ObserveServerMinutes("stuck", 0);
    backoff.ObserveServerMinutes("stuck", 58);
    Check(backoff.IsSuspended("stuck", attemptAt.AddMinutes(delay - 1)), "missing or reappearing old progress preserves suspension");
    attemptAt = attemptAt.AddMinutes(delay);
    Check(!backoff.IsSuspended("stuck", attemptAt), "longer suspension expires exactly");
}
backoff.ObserveServerMinutes("stuck", 59);
Check(backoff.RecordStalledChannel("stuck", null, attemptAt) == attemptAt.AddMinutes(15), "new server minute resets backoff");
backoff.ObserveServerRewardMinutes("stuck", "next-reward", 1);
Check(!backoff.IsSuspended("stuck", attemptAt), "new reward progress resets backoff even below previous reward total");
backoff.RecordStalledChannel("stuck", null, attemptAt);
backoff.ObserveServerRewardMinutes("stuck", "next-reward", 0);
backoff.ObserveServerRewardMinutes("stuck", "next-reward", 1);
Check(backoff.IsSuspended("stuck", attemptAt), "old per-reward minute cannot clear suspension");

Check(TwitchClaimRecoveryPolicy.ShouldProbe(false, false, 58, 60, null), "missing near-complete reward can be verified");
Check(TwitchClaimRecoveryPolicy.ShouldProbe(false, false, 0, 60, "BADGE"), "missing badge can be verified without assumed completion");
Check(TwitchClaimRecoveryPolicy.ShouldProbe(false, false, 0, 60, "EMOTE"), "missing emote can be verified");
Check(!TwitchClaimRecoveryPolicy.ShouldProbe(true, false, 58, 60, "BADGE"), "tracked reward uses normal progress path");
Check(!TwitchClaimRecoveryPolicy.ShouldProbe(false, true, 60, 60, "BADGE"), "claimed reward is not probed again");
Check(TwitchClaimRecoveryPolicy.ShouldProbe(false, false, 0, 60, null), "zero history after restart can be checked without assuming completion");
Check(TwitchClaimRecoveryPolicy.ShouldProbe(false, false, 15, 60, null), "missing ordinary reward can be verified by the server");
Check(TwitchClaimRecoveryPolicy.ProbePriority(0, 60, "BADGE") < TwitchClaimRecoveryPolicy.ProbePriority(58, 60, null), "badge probes take priority");
Check(TwitchClaimRecoveryPolicy.ProbePriority(58, 60, null) < TwitchClaimRecoveryPolicy.ProbePriority(0, 60, null), "near-complete probes precede zero-history probes");
Check(!TwitchClaimRecoveryPolicy.ShouldProbe(false, false, 0, 0, "BADGE"), "invalid watch requirement is rejected");
Check(TwitchClaimRecoveryPolicy.IsConfirmedClaim("ELIGIBLE_FOR_ALL"), "successful claim is confirmed");
Check(TwitchClaimRecoveryPolicy.IsConfirmedClaim("DROP_INSTANCE_ALREADY_CLAIMED"), "already claimed response is confirmed");
foreach (string? status in new string?[] { null, "", "NOT_ELIGIBLE", "DROP_INSTANCE_NOT_FOUND", "UNKNOWN" })
    Check(!TwitchClaimRecoveryPolicy.IsConfirmedClaim(status), "unconfirmed claim status cannot mark reward claimed");
Console.WriteLine("All progressive backoff and missing-reward recovery tests passed.");

var connection = new TwitchConnectionRecoveryPolicy();
Check(connection.CanObserveStall("quinfall", now), "healthy connection allows stall observation");
Check(connection.RecordFailure("quinfall"), "first transport failure is recognized");
Check(!connection.RecordFailure("quinfall"), "repeated failure is not a new interruption");
Check(!connection.CanObserveStall("quinfall", now.AddHours(1)), "transport outage cannot count as a stalled channel");
Check(connection.CanObserveStall("other", now), "connection state is campaign scoped");
Check(connection.RecordSuccess("quinfall", now), "successful transport starts recovery window");
Check(!connection.CanObserveStall("quinfall", now.AddSeconds(31)), "recovered stream cannot be rejected after 31 seconds");
Check(!connection.RecordSuccess("quinfall", now.AddMinutes(1)), "ordinary heartbeats do not extend recovery grace");
Check(!connection.CanObserveStall("quinfall", now.AddMinutes(3).AddTicks(-1)), "full recovery grace is respected");
Check(connection.CanObserveStall("quinfall", now.AddMinutes(3)), "fresh observations resume at exact deadline");
connection.RecordFailure("quinfall");
Check(!connection.CanObserveStall("quinfall", now.AddMinutes(4)), "another interruption cancels observation again");

var xml = System.Xml.Linq.XDocument.Parse(StartupTaskPolicy.BuildXml(@"C:\Apps\Loot & Games\Stream Loot.exe", "S-1-5-21-test", true));
System.Xml.Linq.XNamespace taskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
string? Value(string name) => xml.Descendants(taskNs + name).FirstOrDefault()?.Value;
Check(Value("Command") == @"C:\Apps\Loot & Games\Stream Loot.exe", "startup executable spaces and XML characters are preserved");
Check(Value("WorkingDirectory") == @"C:\Apps\Loot & Games", "startup explicitly sets working directory");
Check(Value("Delay") == "PT30S", "startup waits 30 seconds after logon");
Check(Value("LogonType") == "InteractiveToken" && Value("RunLevel") == "LeastPrivilege", "startup uses current user without password or elevation");
Check(Value("Arguments") == "--autostart --minimize", "startup honors tray preference");
Check(StartupTaskPolicy.Arguments(false) == "--autostart", "normal startup does not force tray mode");
Check(Value("ExecutionTimeLimit") == "PT0S", "scheduler cannot stop mining at its default time limit");
Check(Value("DisallowStartIfOnBatteries") == "false" && Value("StopIfGoingOnBatteries") == "false", "battery power does not block or stop startup");
Console.WriteLine("All connection recovery and startup task tests passed.");
