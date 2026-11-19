namespace SharpAgent;

public partial class MainWindow
{
    async Task<string> AskClarificationAsync(Clarification question, CancellationToken ct)
    {
        CloseSubagents();
        ExpandBrowser(false);
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var choices = new StackPanel();
        foreach (var choice in question.Choices)
        {
            var button = new Button
            {
                Style = Theme.Style("RowButton"), Content = new TextBlock { Text = choice, TextWrapping = TextWrapping.Wrap }, ToolTip = choice,
                Padding = new(12, 6, 12, 6), Margin = new(0, 0, 0, 6), BorderThickness = new(1),
            }.Paint(BackgroundProperty, "Raised").Paint(BorderBrushProperty, "RaisedLine");
            AutomationProperties.SetName(button, choice);
            button.Click += (_, _) => reply.TrySetResult(choice);
            choices.Children.Add(button);
        }
        var custom = new TextBox { Style = Theme.Style("FieldTextBox"), ToolTip = "Введите свой ответ", MaxLength = 4000 };
        AutomationProperties.SetName(custom, "Свой ответ");
        var send = Theme.IconButton("\uE74A", "Отправить свой ответ", 32);
        send.IsEnabled = false;
        send.Margin = new(6, 0, 0, 0);
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
                    Text = question.Question, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10),
                },
                choices,
                new DockPanel { Margin = new(0, 2, 0, 0), Children = { send, custom } },
            },
        };
        _questionCard = new Border
        {
            Style = Theme.Style("Card"), Padding = new(16, 12, 16, 12),
            Child = new ScrollViewer { Content = body, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
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
            if (IsActive)
            {
                ExpandBrowser(false);
                InputBox.Focus();
            }
            else if (_compactChat is { IsActive: true } compact) compact.FocusInput();
        }
    }
}
