using Avalonia;
using Avalonia.Controls;
using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>Hosts a <see cref="Apply.PlanApplyViewModel"/> against whichever tab is active (KON-294).</summary>
public partial class PlanApplyView : UserControl
{
    public static readonly StyledProperty<OpenDocument?> DocumentProperty =
        AvaloniaProperty.Register<PlanApplyView, OpenDocument?>(nameof(Document));

    public PlanApplyView()
    {
        InitializeComponent();
    }

    public OpenDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }
}
