using Avalonia.Controls;
using Kontena.Plugins.ManifestStudio.Git;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>
/// The source-control page (KON-296): branch, ahead/behind, what changed, and the four commands
/// <see cref="GitViewModel"/> exposes. KON-295 built the CLI, the parser and the view model; this is
/// the screen they were missing.
/// <para>
/// Everything Plan §7 leaves out — merge, rebase, conflict resolution, per-hunk staging — is absent
/// here too, and that is the whole design: a second Git client is a product, and its failure mode is
/// losing someone else's work.
/// </para>
/// </summary>
public partial class GitView : UserControl
{
    public GitView()
    {
        InitializeComponent();

        // The status is a fact about the repository, not something the user should have to ask for. A
        // failure lands in the view model's Error, so nothing here can throw into the shell.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GitViewModel vm && vm.RefreshCommand.CanExecute(null))
                vm.RefreshCommand.Execute(null);
        };
    }
}
