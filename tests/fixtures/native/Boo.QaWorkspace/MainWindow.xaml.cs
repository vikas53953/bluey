using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Boo.QaWorkspace;

public partial class MainWindow : Window
{
    private TargetState? _undoState;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void TargetButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActionStatus.Text = "Action observed";
    }

    private void ApplyEdit_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadBrush(ButtonBackground.Text, out var background) ||
            !TryReadBrush(ButtonForeground.Text, out var foreground))
        {
            EditStatus.Text = "Use a valid hex colour such as #2D6CDF.";
            return;
        }

        _undoState = new TargetState(
            TargetButton.Content?.ToString() ?? string.Empty,
            TargetButton.Background,
            TargetButton.Foreground);

        TargetButton.Content = ButtonText.Text;
        TargetButton.Background = background;
        TargetButton.Foreground = foreground;
        AutomationProperties.SetName(TargetButton, ButtonText.Text);
        EditStatus.Text = "Edit applied";
    }

    private void UndoEdit_OnClick(object sender, RoutedEventArgs e)
    {
        if (_undoState is not { } previous)
        {
            EditStatus.Text = "Nothing to undo";
            return;
        }

        TargetButton.Content = previous.Text;
        TargetButton.Background = previous.Background;
        TargetButton.Foreground = previous.Foreground;
        ButtonText.Text = previous.Text;
        ButtonBackground.Text = ToHex(previous.Background);
        ButtonForeground.Text = ToHex(previous.Foreground);
        AutomationProperties.SetName(TargetButton, previous.Text);
        _undoState = null;
        EditStatus.Text = "Edit undone";
    }

    private static bool TryReadBrush(string text, out SolidColorBrush brush)
    {
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color color)
            {
                brush = new SolidColorBrush(color);
                return true;
            }
        }
        catch (FormatException)
        {
            // The fixture reports invalid input in the visible status text.
        }

        brush = new SolidColorBrush(Colors.Transparent);
        return false;
    }

    private static string ToHex(Brush brush)
    {
        var color = ((SolidColorBrush)brush).Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private sealed record TargetState(string Text, Brush Background, Brush Foreground);
}
