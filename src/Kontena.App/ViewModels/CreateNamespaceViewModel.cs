using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "New namespace" modal (KON-464). The namespaces page was read-only, so the only way to get one
/// was to apply a manifest that declared it — which is a strange thing to have to do for an object
/// whose entire content is its name.
/// <para>
/// It applies rather than calling a create of its own: <see cref="IClusterEngine.ApplyAsync"/> is the
/// one door the OAL has for writing, and a namespace is four lines of YAML. The name is validated
/// before it goes in, which is also what keeps it from being anything but a name once it is there.
/// </para>
/// </summary>
public partial class CreateNamespaceViewModel : ViewModelBase
{
    /// <summary>DNS-1123 label, which is what a namespace name is. 63 characters, and the API server agrees.</summary>
    public const int MaxLength = 63;

    private readonly IClusterEngine _cluster;
    private readonly Action _onClose;
    private readonly Func<Task> _onCreated;

    public CreateNamespaceViewModel(IClusterEngine cluster, Action onClose, Func<Task> onCreated)
    {
        _cluster = cluster;
        _onClose = onClose;
        _onCreated = onCreated;
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    /// <summary>
    /// What is wrong with the name, or null when nothing is — same shape as
    /// <see cref="Kontena.Sdk.Orchestration.Provisioning.LocalClusterName"/>, so the field says which
    /// rule was broken instead of the button going quiet for a reason nobody can see. Null while the
    /// box is still empty: a form that is already complaining before anything was typed is noise.
    /// </summary>
    public string? NameProblem => Name.Length == 0 ? null : Problem(Name);

    public bool HasNameProblem => NameProblem is not null;

    public bool CanCreate => !string.IsNullOrWhiteSpace(Name) && Problem(Name) is null && !IsBusy;

    private static string? Problem(string name)
    {
        if (name.Length > MaxLength)
            return $"Use at most {MaxLength} characters.";

        if (name.Any(char.IsUpper))
            return "Use lowercase only — a namespace name is a DNS label, which cannot hold capitals.";

        return Label().IsMatch(name)
            ? null
            : "Use lowercase letters, digits and dashes, starting and ending with a letter or digit.";
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        OnPropertyChanged(nameof(CanCreate));

        // The error described the name as it was when Create was pressed; typing makes it stale.
        if (Error is not null)
            Error = null;
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = Name.Trim();
        if (IsBusy || Problem(name) is not null)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            var failures = new List<string>();
            var bundle = new ManifestBundle
            {
                Yaml = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {name}\n",
                Source = "new namespace",
            };

            await foreach (var step in _cluster.ApplyAsync(bundle))
            {
                if (step.Action == ApplyAction.Failed)
                    failures.Add(step.Error ?? "the cluster refused it without saying why");
            }

            if (failures.Count > 0)
            {
                // Left open on purpose, as the volume modal is: a name that is taken or a namespace
                // someone is not allowed to create is worth correcting in place.
                Error = string.Join("; ", failures);
                return;
            }

            await _onCreated();
            _onClose();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClose();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex Label();
}
