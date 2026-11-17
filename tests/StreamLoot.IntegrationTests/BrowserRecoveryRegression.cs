using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using UI.Views;
using Core.Managers;
using Core.Services;
using System.Runtime.CompilerServices;

static class BrowserRecoveryRegression
{
    // Called on the existing UI regression's STA thread. Never opens the user's browser profile.
    public static void Run()
    {
        Exception? failure = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { failure = new TimeoutException("Browser recovery regression timed out"); Application.Current.Shutdown(); };
        timeout.Start();
        async void Start()
        {
            HiddenWebViewHost? host = null;
            try
            {
                string profile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StreamLoot-browser-test-" + Guid.NewGuid().ToString("N"));
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
                host = new HiddenWebViewHost("isolated test player");
                typeof(HiddenWebViewHost).GetField("_sharedEnvironment", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, environment);
                await host.EnsureInitializedAsync();
                if (host.Owner != null) throw new Exception("Hidden player must not be owned by the main window");
                var owner = Application.Current.MainWindow;
                var previous = owner.WindowState;
                owner.WindowState = WindowState.Minimized;
                await Task.Delay(200);
                if (host.WindowState != WindowState.Normal) throw new Exception("Main-window minimization minimized the player");
                owner.WindowState = previous;
                Console.WriteLine("PASS actual hidden browser remains independent when main window is minimized");
                var old = host.WebView;
                string url = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "TestAssets", "player.html")).AbsoluteUri;
                await host.RecreatePlayerAsync(url);
                if (ReferenceEquals(old, host.WebView)) throw new Exception("Recovery reused the failed controller");
                if (host.WebView.CoreWebView2.Environment.UserDataFolder != profile) throw new Exception("Recovery changed the login profile");
                string result = await Task.Run(() => host.ExecuteScriptAsync("document.querySelector('#fixture').textContent"));
                if (!result.Contains("local fixture")) throw new Exception("Rebuilt browser did not finish navigation");
                Console.WriteLine("PASS actual WebView controller recreated with same isolated profile and background-thread script dispatch");
                var manager = (DropsInventoryManager)RuntimeHelpers.GetUninitializedObject(typeof(DropsInventoryManager));
                void Set(string field, object value) => typeof(DropsInventoryManager).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, value);
                Set("<KickWebView>k__BackingField", host);
                await host.ExecuteScriptAsync("(() => { for(let i=0;i<2;i++){ const v=document.createElement('video'); v.play=()=>{v.dataset.resumed='yes';return Promise.resolve();}; document.body.appendChild(v); } })()");
                await (Task)typeof(DropsInventoryManager).GetMethod("EnsureVideoPlayingAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, new object[] { Core.Enums.Platform.Kick })!;
                string videos = await host.ExecuteScriptAsync("[...document.querySelectorAll('video')].filter(v=>v.muted && v.dataset.resumed==='yes').length");
                if (videos != "2") throw new Exception("Recovery only resumed the first/placeholder video");
                Console.WriteLine("PASS actual recovery resumes loaded player even when a placeholder video comes first");
                string consentScript = (string)typeof(DropsInventoryManager).GetField("RejectKickOptionalCookiesScript", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
                await host.ExecuteScriptAsync("(() => { for(const label of ['I Accept','Reject All','Rozumiem i akceptuję']){ const b=document.createElement('button'); b.innerText=label; b.onclick=()=>{document.body.dataset.clicked=label;}; document.body.appendChild(b); } })()");
                await host.ExecuteScriptAsync(consentScript);
                string consentChoice = await host.ExecuteScriptAsync("document.body.dataset.clicked");
                if (!consentChoice.Contains("Reject All")) throw new Exception("Consent recovery did not reject optional tracking");
                Console.WriteLine("PASS actual consent recovery selects Reject All rather than accepting tracking or chat rules");
                await host.ExecuteScriptAsync("(() => { const vs=[...document.querySelectorAll('video')]; Object.defineProperty(vs[1],'duration',{get:()=>2}); Object.defineProperty(vs[1],'readyState',{get:()=>4}); Object.defineProperty(vs[1],'paused',{get:()=>false}); })()");
                string snapshotScript = (string)typeof(DropsInventoryManager).GetField("PlaybackSnapshotScript", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
                var snapshot = TwitchPlaybackPolicy.Parse(await host.ExecuteScriptAsync(snapshotScript));
                if (snapshot?.ReadyState != 0) throw new Exception("Short helper clip was mistaken for the live stream");
                Console.WriteLine("PASS actual snapshot ignores playing two-second helper clip when main player is empty");
                await host.ExecuteScriptAsync("window.fetch=async(url)=>{ const first=url.endsWith('/first'); await new Promise(r=>setTimeout(r,first?200:10)); return {ok:true,json:async()=>({livestream:{viewer_count:first?11:22}})}; };");
                Task<string> firstStatus = host.FetchKickChannelStatusesAsync(new[] { "first" });
                Task<string> secondStatus = host.FetchKickChannelStatusesAsync(new[] { "second" });
                await Task.WhenAll(firstStatus, secondStatus);
                if (!firstStatus.Result.Contains("first") || firstStatus.Result.Contains("second") || !secondStatus.Result.Contains("second"))
                    throw new Exception("Overlapping Kick status requests consumed each other's response");
                Console.WriteLine("PASS overlapping availability and selection status queries receive only their own browser response");
                var samples = new Dictionary<string, (int Minutes, int FrozenCount, DateTime LastSampleAt)> { ["old"] = (10, 1, DateTime.Now) };
                Set("_creditSync", new object()); Set("_creditTracking", samples);
                Set("_twitchPlaybackRecovery", new PlaybackRecoveryState()); Set("_kickPlaybackRecovery", new PlaybackRecoveryState());
                Set("_twitchCurrentlyOnline", true); Set("_kickCurrentlyOnline", true);
                var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.ReloadCampaignsRequested += () => resumed.SetResult();
                manager.OnSystemSuspend();
                await Task.Run(manager.OnSystemResume);
                await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (samples.Count != 0 || (bool)typeof(DropsInventoryManager).GetField("_systemSuspending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!)
                    throw new Exception("Resume retained stale suspend/stall samples");
                Console.WriteLine("PASS real manager dispatches resume from power-event thread and invalidates stale samples before reload");
            }
            catch (Exception ex) { failure = ex; }
            finally { host?.Dispose(); Application.Current.Shutdown(); }
        }
        Application.Current.Dispatcher.BeginInvoke(new Action(Start));
        // WebView2 requires Application.Run, not only a nested DispatcherFrame.
        Application.Current.Run();
        timeout.Stop();
        if (failure != null) throw new Exception("Actual browser recovery test failed", failure);
    }
}
