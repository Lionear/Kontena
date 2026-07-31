using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The manifest editor as a modal (KON-252), for the kinds that have no detail page to put a tab on
/// — config maps and secrets, which are rows that expand.
/// <para>
/// A wrapper rather than a second editor: the flow, the dry-run and the wording all live in
/// <see cref="ManifestEditorViewModel"/>, and this adds a title, a way out, and a reload of the page
/// underneath once something has actually changed.
/// </para>
/// </summary>
public partial class EditManifestDialogViewModel : ViewModelBase
{
    private readonly Action _onClose;
    private readonly Func<Task> _onDone;

    public EditManifestDialogViewModel(
        IClusterEngine cluster, ResourceRef reference, Action onClose, Func<Task> onDone)
    {
        _onClose = onClose;
        _onDone = onDone;
        Editor = new ManifestEditorViewModel(cluster, reference);
    }

    public ManifestEditorViewModel Editor { get; }

    /// <summary>
    /// Closing reloads the list behind it. Unconditionally, rather than only after a successful
    /// apply: keys and sizes are on that list, an apply changes them, and a list that disagrees with
    /// the editor you just closed is worse than one refresh nobody needed.
    /// </summary>
    [RelayCommand]
    private async Task CloseAsync()
    {
        _onClose();
        await _onDone();
    }
}
