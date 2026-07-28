using Avalonia.Controls;

namespace Kontena.App.Views;

// Tail-following lives on the list itself — see Behaviors/AutoScroll.cs (KON-165).
public partial class ContainerDetailView : UserControl
{
    public ContainerDetailView() => InitializeComponent();
}
