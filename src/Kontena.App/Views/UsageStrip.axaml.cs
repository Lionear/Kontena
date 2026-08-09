using Avalonia.Controls;

namespace Kontena.App.Views;

// The header sparklines (KON-345). Bound to a UsageTrackViewModel, the same one UsagePanel draws.
public partial class UsageStrip : UserControl
{
    public UsageStrip() => InitializeComponent();
}
