using System.Windows;
using Core.Managers;

namespace UI
{
    /// <summary>
    /// First-run onboarding: login steps and — crucially — a pointer to linking game accounts on the drops pages,
    /// which prevents the most common "earned but cannot claim" confusion.
    /// </summary>
    public partial class OnboardingWindow : Window
    {
        public OnboardingWindow()
        {
            InitializeComponent();

            TitleText.Text = Loc.Instance["Onb.Title"];
            IntroText.Text = Loc.Instance["Onb.Intro"];
            Step1Text.Text = Loc.Instance["Onb.Step1"];
            Step2Text.Text = Loc.Instance["Onb.Step2"];
            Step3Text.Text = Loc.Instance["Onb.Step3"];
            StartButton.Content = Loc.Instance["Onb.Start"];
        }

        private void OnTwitchConnectionsClick(object sender, RoutedEventArgs e)
            => Core.Utility.LaunchWeb("https://www.twitch.tv/drops/campaigns");

        private void OnKickConnectionsClick(object sender, RoutedEventArgs e)
            => Core.Utility.LaunchWeb("https://kick.com/drops");

        private void OnStartClick(object sender, RoutedEventArgs e)
        {
            UISettingsManager.Instance.FirstRunCompleted = true;
            Close();
        }
    }
}
