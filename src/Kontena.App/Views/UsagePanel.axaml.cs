using Avalonia.Controls;

namespace Kontena.App.Views;

// The usage charts (KON-345), hosted by either the Metrics tab or the Overview tab depending on
// the placement setting — see ClusterPodDetailView.
public partial class UsagePanel : UserControl
{
    public UsagePanel() => InitializeComponent();
}
