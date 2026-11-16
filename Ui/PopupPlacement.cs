namespace SharpAgent.Ui;

public static class PopupPlacement
{
    const double Gap = 4, CursorHeight = 20;

    public static readonly DependencyProperty InWindowProperty = DependencyProperty.RegisterAttached("InWindow", typeof(bool),
        typeof(PopupPlacement), new PropertyMetadata(false, OnInWindowChanged));

    public static bool GetInWindow(DependencyObject element) => (bool)element.GetValue(InWindowProperty);

    public static void SetInWindow(DependencyObject element, bool value) => element.SetValue(InWindowProperty, value);

    static void OnInWindowChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
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

    static CustomPopupPlacement[] Place(Control popup, UIElement? target, Size size, Size targetSize, double? cursor)
    {
        if (target == null || Window.GetWindow(target)?.Content is not FrameworkElement root || !root.IsAncestorOf(target) ||
            target.RenderSize is { Width: <= 0 } or { Height: <= 0 })
            return [new(new(0, targetSize.Height), PopupPrimaryAxis.Horizontal)];
        var render = target.RenderSize;
        double scaleX = targetSize.Width / render.Width, scaleY = targetSize.Height / render.Height;
        double inset = VisualTreeHelper.GetChildrenCount(popup) > 0 && VisualTreeHelper.GetChild(popup, 0) is FrameworkElement chrome
            ? chrome.Margin.Left
            : 0;
        var origin = target.TranslatePoint(new(), root);
        var mouse = Mouse.GetPosition(target);
        Rect anchor = cursor is { } below && new Rect(render).Contains(mouse)
            ? new(origin.X + mouse.X, origin.Y + mouse.Y, 0, below)
            : new(origin.X - inset, origin.Y - Gap + inset, render.Width + 2 * inset, Math.Max(0, render.Height + 2 * (Gap - inset)));
        double x = Fit(anchor.Left, anchor.Right, size.Width / scaleX, root.ActualWidth);
        double y = Fit(anchor.Bottom, anchor.Top, size.Height / scaleY, root.ActualHeight);
        return [new(new((x - origin.X) * scaleX, (y - origin.Y) * scaleY), PopupPrimaryAxis.None)];
    }

    static double Fit(double start, double end, double length, double limit) =>
        Math.Clamp(start + length <= limit ? start : end - length, 0, Math.Max(0, limit - length));
}
