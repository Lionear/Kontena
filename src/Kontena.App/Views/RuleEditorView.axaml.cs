using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class RuleEditorView : UserControl
{
    public RuleEditorView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Focus opens the whole list; the first keystroke is what turns the value into a query. Wired
    /// here rather than in the view model because "the field got focus" is a view event and nothing
    /// else — the rule it serves lives in <see cref="RuleEditorViewModel.NamespaceTyped"/>.
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (this.FindControl<TextBox>("NamespaceField") is not { } field)
            return;

        field.GotFocus += (_, _) => (DataContext as RuleEditorViewModel)?.OpenNamespaceMenuCommand.Execute(null);
        field.LostFocus += (_, _) => (DataContext as RuleEditorViewModel)?.CloseNamespaceMenuCommand.Execute(null);
        field.TextChanged += (_, _) =>
        {
            if (field.IsFocused)
                (DataContext as RuleEditorViewModel)?.NamespaceTyped();
        };
    }
}
