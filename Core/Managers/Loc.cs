using System.ComponentModel;

namespace Core.Managers
{
    /// <summary>
    /// Lightweight two-language (en/pl) string localizer. Bind from XAML via the <c>{loc:T Key}</c> markup
    /// extension (or <c>{Binding [Key], Source={x:Static managers:Loc.Instance}}</c>), and read in code via
    /// <c>Loc.Instance["Key"]</c>. Switching the language re-raises the indexer binding so bound UI updates live.
    /// </summary>
    public sealed class Loc : INotifyPropertyChanged
    {
        public static Loc Instance { get; } = new Loc();
        public event PropertyChangedEventHandler? PropertyChanged;

        private string _lang = "en";

        public string this[string key] =>
            _map.TryGetValue(key, out (string En, string Pl) t) ? (_lang == "pl" ? t.Pl : t.En) : key;

        public void SetLanguage(string language)
        {
            string normalized = string.Equals(language, "pl", StringComparison.OrdinalIgnoreCase) ? "pl" : "en";
            if (_lang == normalized)
                return;
            _lang = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }

        private static readonly Dictionary<string, (string En, string Pl)> _map = new(StringComparer.Ordinal)
        {
            // ---- Navigation / window ----
            ["Nav.Dashboard"] = ("Dashboard", "Panel"),
            ["Nav.Inventory"] = ("Inventory", "Ekwipunek"),
            ["Nav.Statistics"] = ("Statistics", "Statystyki"),
            ["Nav.Settings"] = ("Settings", "Ustawienia"),
            ["Nav.Help"] = ("Help", "Pomoc"),
            ["Nav.OpenSource"] = ("MIT • open source", "MIT • otwarty kod"),
            ["Win.Minimize"] = ("Minimize", "Minimalizuj"),
            ["Win.Maximize"] = ("Maximize", "Maksymalizuj"),
            ["Win.Close"] = ("Close", "Zamknij"),
            ["Tray.OpenGithub"] = ("Open Github", "Otwórz GitHub"),
            ["Tray.Exit"] = ("Exit", "Zakończ"),
            ["Tray.Restore"] = ("Restore", "Przywróć"),
            ["Common.Live"] = ("live", "na żywo"),
            ["Common.Offline"] = ("offline", "offline"),

            // ---- Miner status ----
            ["Status.Title"] = ("MINER STATUS", "STATUS KOPANIA"),
            ["Status.Idle"] = ("Idle", "Bezczynny"),
            ["Status.IdleDetails"] = ("Waiting for drops", "Oczekiwanie na dropy"),
            ["Status.IdleFiltered"] = ("No live channels for your selected games right now", "Brak transmisji na żywo dla wybranych gier"),
            ["Status.Starting"] = ("Starting", "Uruchamianie"),
            ["Status.StartingDetails"] = ("Finding stream(s) to watch", "Szukanie transmisji do oglądania"),
            ["Status.Evaluating"] = ("Evaluating", "Sprawdzanie"),
            ["Status.EvaluatingDetails"] = ("Checking stream(s) for drops eligibility", "Sprawdzanie transmisji pod kątem dropów"),
            ["Status.Mining"] = ("Mining", "Kopanie"),
            ["Status.MiningDetails"] = ("Watching stream(s) to earn drops", "Oglądanie transmisji, aby zdobywać dropy"),
            ["Status.AllDone"] = ("All campaigns mined and claimed!", "Wszystkie kampanie wykopane i odebrane!"),
            ["Status.IdleNotCrediting"] = ("Nothing to mine", "Nie ma czego kopać"),
            ["Status.IdleNotCreditingDetails"] = ("The remaining campaigns are not crediting (already claimed, or the game account isn't linked). Retrying within the hour.", "Pozostałe kampanie nie naliczają (już odebrane albo konto gry nie jest połączone). Ponowna próba w ciągu godziny."),

            // ---- Dashboard ----
            ["Dash.Campaign"] = ("CAMPAIGN", "KAMPANIA"),
            ["Dash.CurrentDrop"] = ("CURRENT DROP", "AKTUALNY DROP"),
            ["Dash.Watching"] = ("Watching: ", "Oglądane: "),
            ["Dash.ShowLiveChannels"] = ("Show live channels", "Pokaż kanały na żywo"),
            ["Dash.ShowChannels"] = ("Show channels", "Pokaż kanały"),
            ["Dash.ShowChannelsTip"] = ("List live channels for this campaign and click one to switch to it", "Pokaż kanały tej kampanii i kliknij, aby przełączyć"),
            ["Dash.AlsoEarning"] = ("ALSO EARNING ON THIS CHANNEL", "RÓWNOCZEŚNIE ZDOBYWANE NA TYM KANALE"),
            ["Dash.WaitingNoChannel"] = ("Waiting — no live channel", "Oczekiwanie — brak transmisji na żywo"),
            ["Dash.LoginKick"] = ("Login Kick", "Zaloguj Kick"),
            ["Dash.LoginTwitch"] = ("Login Twitch", "Zaloguj Twitch"),
            ["Dash.ShowLiveChannelsBtn"] = ("⟳  Show live channels", "⟳  Pokaż kanały na żywo"),
            ["Dash.ShowChannelsBtn"] = ("⟳  Show channels", "⟳  Pokaż kanały"),
            ["Dash.WatchChannelTip"] = ("Watch this channel for the current campaign", "Oglądaj ten kanał dla bieżącej kampanii"),

            // ---- Inventory ----
            ["Inv.Title"] = ("Inventory", "Ekwipunek"),
            ["Inv.HideClaimed"] = ("Hide claimed", "Ukryj odebrane"),
            ["Inv.HideClaimedTip"] = ("Hide campaigns whose drops are all already claimed", "Ukryj kampanie, których wszystkie dropy są już odebrane"),
            ["Inv.ShowOnlyAvailable"] = ("Show only available", "Tylko dostępne"),
            ["Inv.ShowOnlyAvailableTip"] = ("Hide campaigns whose listed channels are all offline right now", "Ukryj kampanie, których wszystkie kanały są teraz offline"),
            ["Inv.RefreshTip"] = ("Re-check which campaigns have live streamers", "Sprawdź ponownie, które kampanie mają transmisje na żywo"),
            ["Inv.AutoMine"] = ("Auto (mine by priority)", "Auto (kop wg priorytetu)"),
            ["Inv.AutoMineTip"] = ("Clear any pinned campaign and select automatically", "Odepnij wszystkie kampanie i wybieraj automatycznie"),
            ["Inv.MineThis"] = ("Mine this", "Kop to"),
            ["Inv.Unpin"] = ("Unpin", "Odepnij"),
            ["Inv.Rewards"] = ("Rewards", "Nagrody"),
            ["Inv.Ends"] = ("Ends ", "Koniec "),
            ["Inv.EtaTip"] = ("Estimated watch time until the next drop unlocks", "Szacowany czas oglądania do następnego dropa"),
            ["Inv.NotCreditingTip"] = ("Watching this campaign is currently NOT earning server progress (e.g. a broken game-account link or a rerun channel). The miner de-prioritises it automatically.", "Oglądanie tej kampanii NIE nalicza teraz postępu na serwerze (np. zerwane połączenie konta gry lub kanał z retransmisją). Aplikacja automatycznie ją pomija."),
            ["Inv.PinOrderTip"] = ("Position in the pin queue — #1 is mined first, the rest follow automatically.", "Pozycja w kolejce przypięć — #1 kopana jako pierwsza, reszta po kolei."),
            ["Inv.ReadyTip"] = ("Fully watched but not claimed — link your game account on the drops page, then the reward can be collected.", "W pełni obejrzane, ale nieodebrane — połącz konto gry na stronie dropów, wtedy nagrodę da się odebrać."),
            ["Inv.Watching"] = ("WATCHING", "OGLĄDANE"),
            ["Inv.Claimed"] = ("CLAIMED", "ODEBRANE"),
            ["Inv.NoCampaigns"] = ("No active drops campaigns", "Brak aktywnych kampanii z dropami"),
            ["Inv.ChannelSpecific"] = ("Channel-specific", "Kanałowa"),
            ["Inv.ChannelSpecificTip"] = ("Channel-specific: progresses only on the channels listed for this campaign.", "Kanałowa: postęp tylko na kanałach wskazanych w tej kampanii."),
            ["Inv.General"] = ("General", "Ogólna"),
            ["Inv.GeneralTip"] = ("General: progresses on ANY live channel of the game, no matter which one you watch.", "Ogólna: postęp na DOWOLNYM kanale tej gry na żywo, niezależnie który oglądasz."),
            ["Inv.Checking"] = ("Checking…", "Sprawdzanie…"),
            ["Inv.NoStreamers"] = ("No streamers online", "Brak streamerów na żywo"),
            ["Inv.CategoryDrop"] = ("Category drop", "Drop kategorii"),
            ["Inv.NotCrediting"] = ("⚠ NOT CREDITING", "⚠ NIE NALICZA"),
            ["Inv.ReadyBadge"] = ("🔗 READY — connect account to claim", "🔗 GOTOWE — połącz konto, aby odebrać"),

            // ---- Statistics ----
            ["Stats.Title"] = ("Statistics", "Statystyki"),
            ["Stats.WatchedToday"] = ("Watched today", "Obejrzane dziś"),
            ["Stats.Watched7Days"] = ("Last 7 days", "Ostatnie 7 dni"),
            ["Stats.WatchedTotal"] = ("Total watched", "Łącznie obejrzane"),
            ["Stats.DropsClaimed"] = ("Drops claimed", "Odebrane dropy"),
            ["Stats.ClaimHistory"] = ("Claim history", "Historia odbiorów"),
            ["Stats.NoClaims"] = ("No drops claimed yet — they will show up here.", "Brak odebranych dropów — pojawią się tutaj."),

            // ---- Settings ----
            ["Set.Title"] = ("Settings", "Ustawienia"),
            ["Set.General"] = ("General", "Ogólne"),
            ["Set.StartWithWindows"] = ("Start with Windows", "Uruchamiaj razem z Windows"),
            ["Set.MinimizeToTray"] = ("Minimize to system tray on startup", "Minimalizuj do zasobnika przy starcie"),
            ["Set.Appearance"] = ("Appearance", "Wygląd"),
            ["Set.Theme"] = ("Theme", "Motyw"),
            ["Set.ThemeDark"] = ("Dark", "Ciemny"),
            ["Set.ThemeLight"] = ("Light", "Jasny"),
            ["Set.ThemeSystem"] = ("System", "Systemowy"),
            ["Set.Updates"] = ("Updates", "Aktualizacje"),
            ["Set.CheckUpdates"] = ("Check for updates automatically", "Sprawdzaj aktualizacje automatycznie"),
            ["Set.UpdOnLaunch"] = ("Every time the app starts", "Przy każdym uruchomieniu"),
            ["Set.UpdDaily"] = ("Daily", "Codziennie"),
            ["Set.UpdWeekly"] = ("Weekly", "Co tydzień"),
            ["Set.UpdNever"] = ("Never", "Nigdy"),
            ["Set.UpdateAvailable"] = ("A new update is available, update now to experience the newest features!", "Dostępna jest nowa wersja — zaktualizuj, aby korzystać z najnowszych funkcji!"),
            ["Set.UpdateNow"] = ("Update Now", "Aktualizuj teraz"),
            ["Set.DropsBehavior"] = ("Drops Behavior", "Zachowanie dropów"),
            ["Set.AutoClaim"] = ("Auto-claim rewards when ready", "Odbieraj nagrody automatycznie, gdy są gotowe"),
            ["Set.MiningPriority"] = ("Mining priority", "Priorytet kopania"),
            ["Set.PrioAvailability"] = ("Availability + Progress", "Dostępność + postęp"),
            ["Set.PrioEnding"] = ("Ending Soonest", "Kończące się najwcześniej"),
            ["Set.PrioLeastTime"] = ("Least Time To Next Reward", "Najmniej czasu do nagrody"),
            ["Set.PrioHighest"] = ("Highest Completion", "Najwyższe ukończenie"),
            ["Set.GameFiltering"] = ("Game Filtering", "Filtrowanie gier"),
            ["Set.GameFilteringHint"] = ("If no games are selected, all games are allowed.", "Jeśli nie wybrano żadnej gry, dozwolone są wszystkie."),
            ["Set.AllowTip"] = ("Mine this game (allow-list)", "Kop tę grę (biała lista)"),
            ["Set.ExcludeTip"] = ("Never mine this game — overrides everything else", "Nigdy nie kop tej gry — ma pierwszeństwo nad resztą"),
            ["Set.Exclude"] = ("🚫 Exclude", "🚫 Wyklucz"),
            ["Set.ExcludeMode"] = ("Exclude selected games (mine everything except these)", "Wyklucz zaznaczone gry (kop wszystko oprócz nich)"),
            ["Set.ClearTwitchWhitelist"] = ("Clear Twitch whitelist", "Wyczyść listę Twitch"),
            ["Set.ClearTwitchExclusions"] = ("Clear Twitch exclusions", "Wyczyść wykluczenia Twitch"),
            ["Set.ClearKickWhitelist"] = ("Clear Kick whitelist", "Wyczyść listę Kick"),
            ["Set.ClearKickExclusions"] = ("Clear Kick exclusions", "Wyczyść wykluczenia Kick"),
            ["Set.Notifications"] = ("Notifications", "Powiadomienia"),
            ["Set.NotifyReady"] = ("Drop ready to claim (only if auto-claim is off)", "Drop gotowy do odebrania (gdy auto-odbiór wyłączony)"),
            ["Set.NotifyClaimed"] = ("Drop successfully auto-claimed (only if auto-claim is on)", "Drop odebrany automatycznie (gdy auto-odbiór włączony)"),
            ["Set.NotifyUpdate"] = ("New update is available", "Dostępna nowa wersja"),
            ["Set.Advanced"] = ("Advanced", "Zaawansowane"),
            ["Set.SoftwareRendering"] = ("Software rendering (no GPU) — for unstable graphics drivers; takes effect after restart", "Renderowanie programowe (bez GPU) — przy niestabilnych sterownikach grafiki; działa po restarcie"),
            ["Set.SleepWhenDone"] = ("Put the computer to sleep when everything is mined and claimed", "Uśpij komputer, gdy wszystko zostanie wykopane i odebrane"),
            ["Set.Language"] = ("Language / Język:", "Język / Language:"),
            ["Set.Logs"] = ("Logs", "Logi"),
            ["Set.LogsHint"] = ("Open the folder containing diagnostic logs for support tickets.", "Otwórz folder z logami diagnostycznymi do zgłoszeń."),
            ["Set.VerboseLogging"] = ("Verbose debug logging", "Szczegółowe logowanie diagnostyczne"),
            ["Set.VerboseHint"] = ("Adds detailed miner + reward percentage trace logs. Enable only while troubleshooting.", "Dodaje szczegółowe logi kopania i procentów nagród. Włączaj tylko przy diagnozowaniu."),
            ["Set.OpenLogs"] = ("Open Logs Folder", "Otwórz folder logów"),
            ["Set.Accounts"] = ("Accounts", "Konta"),
            ["Set.DangerZone"] = ("DANGER ZONE", "STREFA NIEBEZPIECZNA"),
            ["Set.RemoveHint"] = ("Permanently delete ALL logged-in accounts from this device", "Trwale usuń WSZYSTKIE zalogowane konta z tego urządzenia"),
            ["Set.RemoveAll"] = ("Remove ALL Accounts", "Usuń WSZYSTKIE konta"),
            ["Set.IdeasHere"] = ("Your ideas could be here! (coming soon)", "Tu mogą być Twoje pomysły! (wkrótce)"),
            ["Set.IdeasHint"] = ("Drop your suggestions, to have them implemented here", "Podziel się sugestiami, a trafią do aplikacji"),

            // ---- Help ----
            ["Help.Title"] = ("Help & Support", "Pomoc i wsparcie"),
            ["Help.GettingStarted"] = ("Getting Started", "Pierwsze kroki"),
            ["Help.InventoryTracking"] = ("Inventory Tracking", "Śledzenie ekwipunku"),
            ["Help.Settings"] = ("Settings", "Ustawienia"),
            ["Help.Community"] = ("Support & Community", "Wsparcie i społeczność"),
            ["Help.MadeWith"] = ("Made with rage, caffeine, and zero sleep by Reaxtic", "Zrobione ze złości, kofeiny i zerowej ilości snu przez Reaxtic"),
            ["Help.Fuel"] = ("FUEL THE MACHINE", "DOŁÓŻ DO PIECA"),
            ["Help.Coffee"] = ("BUY ME A COFFEE", "POSTAW MI KAWĘ"),
            ["Help.Step1"] = ("1. Log in to your Twitch & Kick accounts to enable drops tracking.", "1. Zaloguj się na konta Twitch i Kick, aby włączyć śledzenie dropów."),
            ["Help.Step2"] = ("2. Head to the Dashboard to watch your miner status and campaign progress in real time.", "2. Przejdź do Panelu, aby na bieżąco śledzić status kopania i postęp kampanii."),
            ["Help.Step3"] = ("3. Use the sidebar to switch between Inventory, Settings, and this Help page.", "3. Menu po lewej przełącza między Ekwipunkiem, Ustawieniami i tą Pomocą."),
            ["Help.CoffeeText"] = ("This tool is 100% free. Always will be.\nBut if you're farming 24/7 and want to throw some ammo at the dev...", "To narzędzie jest w 100% darmowe. I zawsze będzie.\nAle jeśli farmisz 24/7 i chcesz dorzucić coś twórcy..."),
            ["Help.Closing"] = ("Your drops. My war. Let's keep winning.", "Twoje dropy. Moja wojna. Wygrywajmy dalej."),

            // ---- Onboarding ----
            ["Onb.Title"] = ("Welcome to Stream Loot!", "Witaj w Stream Loot!"),
            ["Onb.Intro"] = ("Three quick steps to start earning drops automatically:", "Trzy szybkie kroki, aby automatycznie zdobywać dropy:"),
            ["Onb.Step1"] = ("1. Log in to Twitch and/or Kick using the buttons on the Dashboard.", "1. Zaloguj się do Twitcha i/lub Kicka przyciskami na Panelu."),
            ["Onb.Step2"] = ("2. Link your game accounts on the drops pages — otherwise earned drops cannot be claimed:", "2. Połącz konta gier na stronach dropów — bez tego zdobytych dropów nie da się odebrać:"),
            ["Onb.Step3"] = ("3. That's it — the app picks campaigns automatically. Pin one with \"Mine this\" to force a favourite.", "3. To wszystko — aplikacja sama wybiera kampanie. Przypnij ulubioną przyciskiem „Kop to”."),
            ["Onb.Start"] = ("Let's go!", "Zaczynamy!"),
            ["Onb.TwitchConnections"] = ("Twitch drops connections", "Połączenia dropów Twitch"),
            ["Onb.KickPage"] = ("Kick drops page", "Strona dropów Kick"),
        };
    }
}
