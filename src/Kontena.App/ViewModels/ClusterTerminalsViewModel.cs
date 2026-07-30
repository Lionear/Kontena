using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Core.Shell;

namespace Kontena.App.ViewModels;

/// <summary>
/// The Terminal page: the shells open on this cluster, one tab each (KON-216).
/// <para>
/// The tabs are the registry's, not this page's — the page is rebuilt on every visit and the shells are
/// not. So this reads the list on construction and writes back which tab was last looked at, and a
/// terminal survives navigating away exactly as it did with one.
/// </para>
/// </summary>
public sealed partial class ClusterTerminalsViewModel : ViewModelBase
{
    private readonly ClusterTerminals _terminals;
    private readonly string _backend;
    private readonly Func<ClusterShellRequest> _request;
    private readonly Func<TerminalFont> _font;
    private readonly Action _onCountChanged;

    public ClusterTerminalsViewModel(
        ClusterTerminals terminals,
        string backend,
        Func<ClusterShellRequest> request,
        Func<TerminalFont> font,
        Action onCountChanged)
    {
        _terminals = terminals;
        _backend = backend;
        _request = request;
        _font = font;
        _onCountChanged = onCountChanged;

        var terminalFont = font();

        foreach (var terminal in terminals.For(backend))
            Terminals.Add(new ClusterTerminalViewModel(terminal, terminalFont));

        // Opening the page with nothing on it would mean a Terminal page that shows no terminal until
        // you press a button to get the thing you already asked for.
        if (Terminals.Count == 0)
            NewTerminal();
        else
            Selected = Terminals.FirstOrDefault(t => t.Terminal.Id == terminals.SelectedFor(backend))
                       ?? Terminals[0];
    }

    /// <summary>The open terminals, oldest first.</summary>
    public ObservableCollection<ClusterTerminalViewModel> Terminals { get; } = [];

    [ObservableProperty]
    private ClusterTerminalViewModel? _selected;

    /// <summary>A tab strip is only worth its room once there is a second tab.</summary>
    public bool HasTabs => Terminals.Count > 1;

    /// <summary>Open another shell on this cluster, on whatever the pickers say now.</summary>
    [RelayCommand]
    public void NewTerminal()
    {
        var terminal = _terminals.Add(_backend, _request());
        var page = new ClusterTerminalViewModel(terminal, _font());

        Terminals.Add(page);
        Selected = page;
        Changed();
    }

    /// <summary>Close a tab, ending its shell — a terminal with no tab is one nobody can reach again.</summary>
    [RelayCommand]
    public async Task CloseAsync(ClusterTerminalViewModel page)
    {
        var index = Terminals.IndexOf(page);
        Terminals.Remove(page);
        await _terminals.CloseAsync(page.Terminal);

        if (Terminals.Count == 0)
        {
            // The page cannot be empty, so closing the last tab opens the next one rather than leaving
            // a Terminal page with no terminal on it.
            NewTerminal();
            return;
        }

        // Land on the neighbour rather than jumping to the front: closing the third of four tabs and
        // ending up on the first loses your place for no reason.
        Selected = Terminals[Math.Min(index, Terminals.Count - 1)];
        Changed();
    }

    /// <summary>Switch to a tab.</summary>
    [RelayCommand]
    public void Select(ClusterTerminalViewModel page) => Selected = page;

    partial void OnSelectedChanged(ClusterTerminalViewModel? value)
    {
        foreach (var page in Terminals)
            page.IsCurrent = ReferenceEquals(page, value);

        if (value is not null)
            _terminals.Select(value.Terminal);
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasTabs));
        _onCountChanged();
    }
}
