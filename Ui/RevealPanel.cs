using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SharpAgent.Ui;

public sealed class RevealPanel : Decorator
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(RevealPanel), new PropertyMetadata(false, OpenChanged));

    private static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        "Fraction", typeof(double), typeof(RevealPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public RevealPanel()
    {
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private double Fraction => (double)GetValue(FractionProperty);

    private static void OpenChanged(DependencyObject instance, DependencyPropertyChangedEventArgs e) =>
        ((RevealPanel)instance).Reveal((bool)e.NewValue);

    private void Reveal(bool open)
    {
        var start = Fraction;
        var target = open ? 1d : 0d;
        BeginAnimation(FractionProperty, null);
        SetValue(FractionProperty, target);
        IsHitTestVisible = open;
        if (!IsLoaded || !Theme.AnimationsEnabled || Math.Abs(start - target) < .001)
        {
            Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var motion = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(open ? 220 : 170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
        };
        motion.Completed += (_, _) =>
        {
            if (!IsOpen) Visibility = Visibility.Collapsed;
        };
        BeginAnimation(FractionProperty, motion);
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null) return default;
        Child.Measure(new Size(available.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Fraction);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child != null) Child.Arrange(new Rect(0, 0, final.Width, Child.DesiredSize.Height));
        return final;
    }
}
