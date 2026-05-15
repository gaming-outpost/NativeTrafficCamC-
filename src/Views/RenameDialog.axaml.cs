using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace CoastalCommandCenter.Views;

public partial class RenameDialog : Window
{
    public string? NewName { get; private set; }

    public RenameDialog() : this(string.Empty) { }

    public RenameDialog(string currentName)
    {
        InitializeComponent();

        var nameBox = this.FindControl<TextBox>("NameBox")!;
        nameBox.Text = currentName;
        nameBox.SelectAll();
        nameBox.Focus();
    }

    private void OkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        NewName = this.FindControl<TextBox>("NameBox")?.Text;
        Close(true);
    }

    private void CancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(false);
    }

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NewName = this.FindControl<TextBox>("NameBox")?.Text;
            Close(true);
        }
        else if (e.Key == Key.Escape)
        {
            Close(false);
        }
    }
}
