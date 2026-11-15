using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SharpAgent.Agent;
using SharpAgent.Core;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private async Task<string> AskClarificationAsync(Clarification question, CancellationToken ct)
    {
        CloseSubagents();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = question.Question, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7),
        });
        var choices = new WrapPanel();
        foreach (var choice in question.Choices)
        {
            var button = new Button
            {
                Style = Theme.Style("RowButton"), Content = new TextBlock { Text = choice, TextWrapping = TextWrapping.Wrap },
                ToolTip = choice, MaxWidth = 360, Background = Theme.Surface2, Margin = new Thickness(0, 0, 6, 5),
            };
            AutomationProperties.SetName(button, choice);
            button.Click += (_, _) => reply.TrySetResult(choice);
            choices.Children.Add(button);
        }
        body.Children.Add(choices);

        var custom = new TextBox { Style = Theme.Style("FieldTextBox"), ToolTip = "Введите свой ответ", MaxLength = 4000 };
        AutomationProperties.SetName(custom, "Свой ответ");
        var send = Theme.IconButton("\uE74A", "Отправить свой ответ", 32);
        send.IsEnabled = false;
        send.Margin = new Thickness(5, 0, 0, 0);
        custom.TextChanged += (_, _) => send.IsEnabled = !string.IsNullOrWhiteSpace(custom.Text);
        send.Click += (_, _) => reply.TrySetResult(custom.Text.Trim());
        custom.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !send.IsEnabled) return;
            e.Handled = true;
            reply.TrySetResult(custom.Text.Trim());
        };
        var entry = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        entry.Children.Add(send);
        entry.Children.Add(custom);
        body.Children.Add(entry);
        QuestionHost.Content = new Border
        {
            Child = new ScrollViewer { Content = body, MaxHeight = 185, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Background = Theme.Surface, BorderBrush = Theme.LineStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8),
        };
        QuestionHost.Visibility = Visibility.Visible;
        _ = Dispatcher.InvokeAsync(() => ((Button)choices.Children[0]).Focus(), DispatcherPriority.Input);
        try
        {
            return await reply.Task.WaitAsync(ct);
        }
        finally
        {
            QuestionHost.Visibility = Visibility.Collapsed;
            QuestionHost.Content = null;
            if (IsActive) InputBox.Focus();
        }
    }
}
