using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using Core.Managers;
using Core.Services;
using Core.Logging;

namespace UI.Views;

public partial class TwitchAuthorizationWindow : Window
{
    private readonly TwitchGqlService _service;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _openedCode;
    public event Action? AuthorizationCompleted;
    public TwitchAuthorizationWindow(TwitchGqlService service)
    {
        InitializeComponent();
        _service = service;
        service.DeviceAuthorization.StateChanged += OnStateChanged;
        _clock.Tick += (_, _) => UpdateState();
        _clock.Start();
        Closed += (_, _) => { _clock.Stop(); service.DeviceAuthorization.StateChanged -= OnStateChanged; };
        Loaded += async (_, _) =>
        {
            UpdateState();
            if (service.DeviceAuthorization.State.Stage is not (TwitchAuthorizationStage.Waiting or TwitchAuthorizationStage.Requesting))
                await BeginAsync();
        };
    }

    private void OnStateChanged(TwitchAuthorizationState state) => Dispatcher.InvokeAsync(UpdateState);
    private async Task BeginAsync()
    {
        try { if (await _service.AuthorizeDeviceAsync()) AuthorizationCompleted?.Invoke(); }
        catch (Exception ex) { AppLogger.Warn("TwitchAuthorization", $"Could not finish authorization: {ex.Message}"); StatusText.Text = Loc.Instance["Auth.Failed"]; }
    }
    private void UpdateState()
    {
        var state = _service.DeviceAuthorization.State;
        string key = state.Stage switch {
            TwitchAuthorizationStage.Requesting => "Auth.Requesting", TwitchAuthorizationStage.Waiting => "Auth.Waiting",
            TwitchAuthorizationStage.Approved => "Auth.Approved", TwitchAuthorizationStage.Expired => "Auth.Expired",
            TwitchAuthorizationStage.Cancelled => "Auth.Cancelled", TwitchAuthorizationStage.Failed => "Auth.Failed", _ => "Auth.Required" };
        StatusText.Text = Loc.Instance[key];
        bool waiting = state.Stage == TwitchAuthorizationStage.Waiting && state.ExpiresAt > DateTimeOffset.UtcNow;
        CodeText.Text = waiting ? state.UserCode : "";
        TimeText.Text = waiting ? $"{Loc.Instance["Auth.TimeLeft"]}: {Math.Max(0, (int)(state.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalSeconds) / 60:00}:{Math.Max(0, (int)(state.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalSeconds) % 60:00}" : "";
        OpenButton.IsEnabled = CopyButton.IsEnabled = waiting;
        bool running = state.Stage is TwitchAuthorizationStage.Requesting or TwitchAuthorizationStage.Waiting;
        NewButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        if (waiting && _openedCode != state.UserCode)
        {
            _openedCode = state.UserCode;
            OpenPage(); // only after the user explicitly starts authorization
        }
    }
    private void OpenPage()
    {
        try { if (_service.DeviceAuthorization.State.VerificationUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Warn("TwitchAuthorization", $"Browser could not be opened: {ex.Message}"); }
    }
    private void OnOpen(object sender, RoutedEventArgs e) => OpenPage();
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { if (CodeText.Text.Length > 0) System.Windows.Clipboard.SetText(CodeText.Text); }
        catch (Exception ex) { AppLogger.Warn("TwitchAuthorization", $"Clipboard is unavailable: {ex.Message}"); }
    }
    private async void OnNewCode(object sender, RoutedEventArgs e) => await BeginAsync();
    private void OnCancel(object sender, RoutedEventArgs e) => _service.DeviceAuthorization.Cancel();
}
