using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "New network" modal (KON-92). Networks could be listed and removed but not created, so putting
/// two containers on a network of your own meant creating it outside Kontena first.
/// </summary>
public partial class CreateNetworkViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Func<Task> _onCreated;

    public CreateNetworkViewModel(IContainerEngine engine, Action onClose, Func<Task> onCreated)
    {
        _engine = engine;
        _onClose = onClose;
        _onCreated = onCreated;
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _driver = "bridge";
    [ObservableProperty] private string _subnet = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    /// <summary>
    /// The drivers that can actually be created on a desktop engine. <c>host</c> and <c>none</c> are
    /// deliberately absent: they exist once, are provided by the engine, and cannot be made.
    /// <c>overlay</c> is absent too — it needs Swarm, so offering it here would fail at the socket.
    /// </summary>
    public string[] Drivers { get; } = ["bridge", "macvlan", "ipvlan"];

    /// <summary>Left empty, the engine picks a subnet from its own pool — which is the usual case.</summary>
    public bool CanCreate => !string.IsNullOrWhiteSpace(Name) && !IsBusy && SubnetProblem is null;

    /// <summary>
    /// What is wrong with the entered subnet, or null when it is empty or valid. Checked here rather
    /// than left to the engine: "invalid CIDR" is knowable without a round trip, and the message the
    /// daemon gives back for it is considerably less clear than saying so up front.
    /// </summary>
    public string? SubnetProblem
    {
        get
        {
            var text = Subnet.Trim();
            if (text.Length == 0)
                return null;

            return IPNetwork.TryParse(text, out _)
                ? null
                : "Enter a subnet in CIDR form, e.g. 172.28.0.0/16.";
        }
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreate));
        if (Error is not null)
            Error = null;
    }

    partial void OnSubnetChanged(string value)
    {
        OnPropertyChanged(nameof(SubnetProblem));
        OnPropertyChanged(nameof(CanCreate));
        if (Error is not null)
            Error = null;
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = Name.Trim();
        if (string.IsNullOrWhiteSpace(name) || IsBusy || SubnetProblem is not null)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            var subnet = Subnet.Trim();
            await _engine.CreateNetworkAsync(new CreateNetworkRequest
            {
                Name = name,
                Driver = string.IsNullOrWhiteSpace(Driver) ? "bridge" : Driver.Trim(),
                Subnet = subnet.Length == 0 ? null : subnet,
            });

            await _onCreated();
            _onClose();
        }
        catch (Exception ex)
        {
            // Stays open: an overlapping subnet or a taken name is worth fixing in place.
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClose();
}
