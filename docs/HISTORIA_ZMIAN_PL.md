# Stream Loot — historia rozwoju do wersji 1.1.18

Stream Loot rozwija projekt Stream Drop Collector, udostępniony przez Marcusa Jensena na licencji MIT. Aplikacja obsługuje oglądanie transmisji i odbieranie dropów na Twitchu oraz Kicku. Poniższy opis obejmuje rozwój linii Stream Loot od wersji 1.1.0 do 1.1.18; szczegółowe wpisy dla poszczególnych wydań znajdują się w [CHANGELOG.md](../CHANGELOG.md).

## Podstawowe funkcje i niezawodność — 1.1.0–1.1.2

Dodano stronę statystyk z czasem oglądania oraz historią odebranych nagród, kolejkę przypiętych kampanii i szacowany czas do kolejnego dropu. Rozbudowano informacje w zasobniku systemowym, powiadomienia oraz opcjonalne uśpienie komputera po zakończeniu dostępnych kampanii.

Wprowadzono odzyskiwanie działania WebView2 po awarii renderera lub procesu przeglądarki, opcję renderowania programowego oraz automatyczne aktualizacje z paczek GitHub Releases. Watchdog sprawdza aktywność silnika i może ponownie uruchomić aplikację, gdy przestanie ona odpowiadać. Dodano obsługę uśpienia i wznowienia Windows oraz ponowne ładowanie kampanii po wznowieniu.

Poprawiono niezależną obsługę przypięć dla Twitcha i Kicka, przechodzenie do kolejnych dostępnych przypięć i zachowanie kolejki przy przejściowych błędach pobierania. Usunięto pętlę ponownego wyboru kanałów Kick, która mogła przerywać naliczanie czasu.

## Odbieranie dropów i przechodzenie do kolejnych nagród — 1.1.3

Żądania odbioru nagród Kick otrzymały odrębne identyfikatory, aby odpowiedź dotycząca postępu lub statusu kanału nie została pomylona z wynikiem odbioru. Dodano bezpieczną obsługę pustych i nieprawidłowych odpowiedzi oraz odpowiedzi „już odebrano”.

Gdy kanał przestaje naliczać postęp, aplikacja może przejść do innego kwalifikującego się streamera zamiast odrzucać całą kampanię. Pozwala to kontynuować kolejne nagrody w tej samej kampanii.

## Rzeczywisty postęp i bezpieczniejszy restart — 1.1.4–1.1.5

Postęp Twitcha jest uzgadniany z serwerem. Samo poprawne wysłanie zdarzenia oglądania nie dodaje już lokalnie minuty, której Twitch mógł nie zaliczyć. Procenty są zaokrąglane w dół: 59 z 60 minut oznacza 98%, a nie ukończenie kampanii.

Nieudany lub oczekujący odbiór nie jest automatycznie przedstawiany jako brak połączenia konta gry. Po odrzuceniu odbioru aplikacja ponownie sprawdza postęp. Dodano rejestrowanie powodów zamknięcia i wykrywanie poprzedniego uruchomienia zakończonego bez poprawnego wyjścia.

Restart watchdoga wymaga potwierdzenia gotowości nowej instancji przed zamknięciem dotychczasowej. Nieudany restart pozostawia bieżący proces uruchomiony. Po regresji w kopaniu Twitcha dalsze poprawki oparto na stabilnej wersji 1.1.3, z ponownym włączeniem poprawionych mechanizmów postępu i odzyskiwania.

Poprawiono oczekiwanie na pełne pobranie odpowiedzi kampanii w WebView2 oraz obsługę błędów integralności Twitcha. Dodano alternatywną autoryzację urządzenia; jej token jest szyfrowany dla bieżącego użytkownika Windows. Błędna odpowiedź nie jest traktowana jak poprawna, pusta lista kampanii.

## Przypięcia i wybór kwalifikujących się transmisji — 1.1.6–1.1.11

Przypięte kampanie są sprawdzane pod kątem dostępnych streamerów. Niedostępne przypięcie nie blokuje innych przypiętych kampanii ani wyboru zapasowego. Aplikacja okresowo sprawdza, czy może wrócić do przypiętej kampanii.

Dodano weryfikację kanałów Twitch przez GraphQL, gdy strona odtwarzacza nie udostępnia informacji o kategorii. Ostatecznie wybór kanału wymaga także obecności w katalogu transmisji z aktywnymi dropami (`DROPS_ENABLED`). Samo nadawanie w odpowiedniej kategorii nie wystarcza.

Kanał Twitch, który nie nalicza postępu, jest pomijany bez automatycznego blokowania całej kampanii. Dla ogólnych kampanii Kick dodano sprawdzanie kategorii przez API, liczbę wykrytych transmisji i listę streamerów do wyboru.

W automatycznym wyborze wprowadzono kończenie rozpoczętych nagród według rzeczywistego czasu pozostałego do następnego dropu. Ręczne przypięcia i późniejsze priorytety gier mają pierwszeństwo przed tą heurystyką.

## Widoczność kampanii, odbiory i dostępność — 1.1.12–1.1.14

Kampanie Twitch nie są ukrywane wyłącznie z powodu niepołączonego konta gry. Niektóre pozwalają zbierać postęp przed połączeniem; odbiór lub dostarczenie nagrody nadal zależy od zasad kampanii.

Sprawdzanie dostępności współdzieli zapytania dla tej samej gry, stosuje limity czasu i aktualizuje oznaczenia kampanii stopniowo. Szybkie zmiany wykluczeń są łączone w jedną ponowną ocenę wyboru, z zachowaniem dotychczasowego postępu.

Wynik odbioru dropu Twitch jest odczytywany ze statusu żądania, a nie z informacji o połączeniu konta gry. Potwierdzone odbiory są zapisywane trwale, również gdy pochodzą z odświeżenia ekwipunku. Wspólne nagrody są rozpoznawane po identyfikatorach i okresie przyznania, bez uznawania samej nazwy nagrody za dowód odbioru.

Rejestr odbiorów ogranicza ponowne wybieranie ukończonych kampanii po odświeżeniu lub restarcie. Nie odtwarza jednak automatycznie każdego historycznego odbioru, którego Twitch nie zwraca i którego aplikacja wcześniej nie zapisała.

## Priorytety gier i wygodniejsza konfiguracja — 1.1.15–1.1.18

Białą listę zastąpiła uporządkowana lista preferowanych gier, osobna dla Twitcha i Kicka. Gry wybiera się w ustawieniach, a ich kolejność zmienia strzałkami. Przykładowo Rust na pozycji #1 otrzymuje pierwszeństwo przed AirMech na pozycji #2, jeśli ma kampanię i kwalifikującą się transmisję.

Niedostępna gra nie blokuje następnych pozycji. Gdy żadna preferowana gra nie jest dostępna, aplikacja może wybrać pozostałe gry. Powrót gry o wyższym priorytecie jest sprawdzany około co trzy minuty.

Wykluczenia pozostały niezależne od priorytetów. Zasady wyboru są następujące:

1. Wykluczone gry nie są kopane, również po przypięciu kampanii.
2. Dostępna przypięta kampania ma pierwszeństwo przed automatycznymi priorytetami gier.
3. Pozostałe kampanie są wybierane według kolejności gier, z przejściem do dostępnego wyboru zapasowego.
4. W obrębie danego priorytetu preferowane są rozpoczęte nagrody bliższe ukończenia, a dalej stosowany jest wybrany tryb automatyczny.

Dodano wyszukiwanie po nazwie i ukrywanie wykluczonych gier. Usunięto zbiorczy przełącznik „wyklucz zaznaczone”; dotychczasowe wykluczenia są zachowywane. Poprawiono odświeżanie i sortowanie listy po kliknięciu, zmianie kolejności oraz opóźnionej przebudowie przez silnik kopania.

Pod listami Twitch i Kick dodano uchwyty do niezależnego powiększania i pomniejszania ich wysokości. Rozmiary są zapisywane po zakończeniu przeciągania i przywracane po restarcie.

## Dystrybucja i ograniczenia

Wydania GitHub zawierają samodzielną paczkę Windows x64, budowaną ze źródeł. Profile WebView2, tokeny użytkownika, lokalne ustawienia, rejestry odebranych nagród i logi nie są częścią paczki wydania.

Aplikacja nie omija warunków kampanii ani zasad Twitcha i Kicka. Dostępność właściwej transmisji, potwierdzenie postępu przez platformę oraz wymagane połączenie konta gry nadal decydują o możliwości zdobycia i dostarczenia nagrody.
