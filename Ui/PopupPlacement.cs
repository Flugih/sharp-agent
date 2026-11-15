using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SharpAgent.Ui;

public static class PopupPlacement
{
    private const double Gap = 4, CursorHeight = 20;

    public static readonly DependencyProperty InWindowProperty = DependencyProperty.RegisterAttached(
        "InWindow", typeof(bool), typeof(PopupPlacement), new PropertyMetadata(false, OnInWindowChanged));

    public static bool GetInWindow(DependencyObject element) => (bool)element.GetValue(InWindowProperty);

    public static void SetInWindow(DependencyObject element, bool value) => element.SetValue(InWindowProperty, value);

    private static void OnInWindowChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        if (element is ContextMenu menu)
        {
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (size, target, _) =>
                Place(menu, menu.PlacementTarget, size, target, menu.PlacementTarget is ButtonBase ? null : 0);
        }
        else if (element is ToolTip tip)
        {
            tip.Placement = PlacementMode.Custom;
            tip.CustomPopupPlacementCallback = (size, target, _) => Place(tip, tip.PlacementTarget, size, target, CursorHeight);
        }
    }

    private static CustomPopupPlacement[] Place(Control popup, UIElement? target, Size size, Size targetSize, double? cursor)
    {
        if (target == null || Window.GetWindow(target)?.Content is not FrameworkElement root || !root.IsAncestorOf(target) ||
            target.RenderSize.Width <= 0 || target.RenderSize.Height <= 0)
            return [new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal)];

        double scaleX = targetSize.Width / target.RenderSize.Width, scaleY = targetSize.Height / target.RenderSize.Height;
        double width = size.Width / scaleX, height = size.Height / scaleY;
        double inset = VisualTreeHelper.GetChildrenCount(popup) > 0 && VisualTreeHelper.GetChild(popup, 0) is FrameworkElement chrome
            ? chrome.Margin.Left
            : 0;
        var origin = target.TranslatePoint(new Point(), root);
        var mouse = Mouse.GetPosition(target);
        var anchor = cursor is { } below && new Rect(target.RenderSize).Contains(mouse)
            ? new Rect(origin.X + mouse.X, origin.Y + mouse.Y, 0, below)
            : new Rect(origin.X - inset, origin.Y - Gap + inset, target.RenderSize.Width + 2 * inset,
                Math.Max(0, target.RenderSize.Height + 2 * (Gap - inset)));

        double x = anchor.Left + width <= root.ActualWidth ? anchor.Left : anchor.Right - width;
        double y = anchor.Bottom + height <= root.ActualHeight ? anchor.Bottom : anchor.Top - height;
        x = Math.Clamp(x, 0, Math.Max(0, root.ActualWidth - width));
        y = Math.Clamp(y, 0, Math.Max(0, root.ActualHeight - height));
        return [new CustomPopupPlacement(new Point((x - origin.X) * scaleX, (y - origin.Y) * scaleY), PopupPrimaryAxis.None)];
    }
}
