using System.ComponentModel;

namespace Core.Managers
{
    /// <summary>
    /// Lightweight two-language (en/pl) string localizer. Bind from XAML via
    /// <c>{Binding [Key], Source={x:Static managers:Loc.Instance}}</c> or read in code via
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
            // Navigation
            ["Nav.Dashboard"] = ("Dashboard", "Panel"),
            ["Nav.Inventory"] = ("Inventory", "Ekwipunek"),
            ["Nav.Statistics"] = ("Statistics", "Statystyki"),
            ["Nav.Settings"] = ("Settings", "Ustawienia"),
            ["Nav.Help"] = ("Help", "Pomoc"),

            // Miner status
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

            // Dashboard
            ["Dash.Campaign"] = ("CAMPAIGN", "KAMPANIA"),
            ["Dash.CurrentDrop"] = ("CURRENT DROP", "AKTUALNY DROP"),
            ["Dash.Watching"] = ("Watching:", "Oglądane:"),
            ["Dash.ShowLiveChannels"] = ("Show live channels", "Pokaż kanały na żywo"),
            ["Dash.ShowChannels"] = ("Show channels", "Pokaż kanały"),
            ["Dash.AlsoEarning"] = ("ALSO EARNING ON THIS CHANNEL", "RÓWNOCZEŚNIE ZDOBYWANE NA TYM KANALE"),
            ["Dash.WaitingNoChannel"] = ("Waiting — no live channel", "Oczekiwanie — brak transmisji na żywo"),
            ["Dash.DropEta"] = ("Drop in ~", "Drop za ~"),

            // Inventory
            ["Inv.Title"] = ("Inventory", "Ekwipunek"),
            ["Inv.HideClaimed"] = ("Hide claimed", "Ukryj odebrane"),
            ["Inv.ShowOnlyAvailable"] = ("Show only available", "Tylko dostępne"),
            ["Inv.AutoMine"] = ("Auto (mine by priority)", "Auto (kop wg priorytetu)"),
            ["Inv.MineThis"] = ("Mine this", "Kop to"),
            ["Inv.Unpin"] = ("Unpin", "Odepnij"),
            ["Inv.Rewards"] = ("Rewards", "Nagrody"),
            ["Inv.Ends"] = ("Ends", "Koniec"),

            // Statistics
            ["Stats.Title"] = ("Statistics", "Statystyki"),
            ["Stats.WatchedToday"] = ("Watched today", "Obejrzane dziś"),
            ["Stats.Watched7Days"] = ("Last 7 days", "Ostatnie 7 dni"),
            ["Stats.WatchedTotal"] = ("Total watched", "Łącznie obejrzane"),
            ["Stats.DropsClaimed"] = ("Drops claimed", "Odebrane dropy"),
            ["Stats.ClaimHistory"] = ("Claim history", "Historia odbiorów"),
            ["Stats.NoClaims"] = ("No drops claimed yet — they will show up here.", "Brak odebranych dropów — pojawią się tutaj."),

            // Settings
            ["Set.SoftwareRendering"] = ("Software rendering (no GPU) — for unstable graphics drivers; takes effect after restart", "Renderowanie programowe (bez GPU) — przy niestabilnych sterownikach grafiki; działa po restarcie"),
            ["Set.SleepWhenDone"] = ("Put the computer to sleep when everything is mined and claimed", "Uśpij komputer, gdy wszystko zostanie wykopane i odebrane"),
            ["Set.Language"] = ("Language", "Język"),

            // Onboarding
            ["Onb.Title"] = ("Welcome to Stream Loot!", "Witaj w Stream Loot!"),
            ["Onb.Intro"] = ("Three quick steps to start earning drops automatically:", "Trzy szybkie kroki, aby automatycznie zdobywać dropy:"),
            ["Onb.Step1"] = ("1. Log in to Twitch and/or Kick using the buttons on the Dashboard.", "1. Zaloguj się do Twitcha i/lub Kicka przyciskami na Panelu."),
            ["Onb.Step2"] = ("2. Link your game accounts on the drops pages — otherwise earned drops cannot be claimed:", "2. Połącz konta gier na stronach dropów — bez tego zdobytych dropów nie da się odebrać:"),
            ["Onb.Step3"] = ("3. That's it — the app picks campaigns automatically. Pin one with \"Mine this\" to force a favourite.", "3. To wszystko — aplikacja sama wybiera kampanie. Przypnij ulubioną przyciskiem „Kop to”."),
            ["Onb.Start"] = ("Let's go!", "Zaczynamy!"),
        };
    }
}
