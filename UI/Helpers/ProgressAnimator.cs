using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows;

namespace UI.Helpers
{
    /// <summary>
    /// Attached property that animates a ProgressBar's value instead of snapping it, so progress changes
    /// (including the periodic server reconciliation) glide smoothly rather than jumping.
    /// Usage: <c>helpers:ProgressAnimator.AnimatedValue="{Binding Percent}"</c> instead of <c>Value=</c>.
    /// </summary>
    public static class ProgressAnimator
    {
        public static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.RegisterAttached(
            "AnimatedValue", typeof(double), typeof(ProgressAnimator),
            new PropertyMetadata(0d, OnAnimatedValueChanged));

        public static double GetAnimatedValue(DependencyObject obj) => (double)obj.GetValue(AnimatedValueProperty);
        public static void SetAnimatedValue(DependencyObject obj, double value) => obj.SetValue(AnimatedValueProperty, value);

        private static void OnAnimatedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not RangeBase bar)
                return;

            DoubleAnimation animation = new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(RangeBase.ValueProperty, animation);
        }
    }
}
