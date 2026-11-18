namespace SharpAgent;

public partial class MainWindow
{
    async Task<string> AskClarificationAsync(Clarification question, CancellationToken ct)
    {
        CloseSubagents();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var choices = new WrapPanel();
        foreach (var choice in question.Choices)
        {
            var button = new Button
            {
                Style = Theme.Style("RowButton"), Content = new TextBlock { Text = choice, TextWrapping = TextWrapping.Wrap }, ToolTip = choice,
                MaxWidth = 360, Margin = new(0, 0, 6, 5),
            }.Paint(BackgroundProperty, "Surface2");
            AutomationProperties.SetName(button, choice);
            button.Click += (_, _) => reply.TrySetResult(choice);
            choices.Children.Add(button);
        }
        var custom = new TextBox { Style = Theme.Style("FieldTextBox"), ToolTip = "Введите свой ответ", MaxLength = 4000 };
        AutomationProperties.SetName(custom, "Свой ответ");
        var send = Theme.IconButton("\uE74A", "Отправить свой ответ", 32);
        send.IsEnabled = false;
        send.Margin = new(5, 0, 0, 0);
        DockPanel.SetDock(send, Dock.Right);
        custom.TextChanged += (_, _) => send.IsEnabled = !string.IsNullOrWhiteSpace(custom.Text);
        send.Click += (_, _) => reply.TrySetResult(custom.Text.Trim());
        custom.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !send.IsEnabled) return;
            e.Handled = true;
            reply.TrySetResult(custom.Text.Trim());
        };
        var body = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = question.Question, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                    Margin = new(0, 0, 0, 7),
                },
                choices,
                new DockPanel { Margin = new(0, 2, 0, 0), Children = { send, custom } },
            },
        };
        _questionCard = new Border
        {
            Child = new ScrollViewer { Content = body, MaxHeight = 185, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderThickness = new(1), CornerRadius = new(10), Padding = new(12, 8, 12, 8),
        }.Paint(BackgroundProperty, "Surface").Paint(BorderBrushProperty, "LineStrong");
        _questionFocus = (Button)choices.Children[0];
        _compactChat?.SetStatus("Жду ответа");
        PlaceQuestion();
        try
        {
            return await reply.Task.WaitAsync(ct);
        }
        finally
        {
            _questionCard = _questionFocus = null;
            PlaceQuestion();
            if (IsActive) InputBox.Focus();
            else if (_compactChat is { IsActive: true } compact) compact.FocusInput();
        }
    }
}
