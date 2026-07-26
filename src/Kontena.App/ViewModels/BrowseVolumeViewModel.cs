using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>One row in the volume browser.</summary>
public sealed class VolumeEntryViewModel
{
    public VolumeEntryViewModel(VolumeEntry entry)
    {
        Name = entry.Name;
        IsDirectory = entry.IsDirectory;
        SizeText = entry.IsDirectory ? "—" : Format.Size(entry.SizeBytes);
        ModifiedText = entry.ModifiedAt is { } at ? Format.Duration(DateTimeOffset.UtcNow - at) + " ago" : "—";
        IconKey = entry.IsDirectory ? "IconFolder" : "IconLogs";
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }
    public string IconKey { get; }
}

/// <summary>
/// The volume browser (KON-90): what is inside a volume, one directory at a time.
/// <para>
/// Read-only by design. Nothing here writes, moves or deletes — the engine reads the volume through a
/// container that is never started, and letting the UI change files would need a very different and
/// much more dangerous mechanism.
/// </para>
/// </summary>
public partial class BrowseVolumeViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;
    private readonly Action _onClose;

    public BrowseVolumeViewModel(IContainerEngine engine, string volumeName, Action onClose)
    {
        _engine = engine;
        _onClose = onClose;
        VolumeName = volumeName;
        _ = LoadAsync("/");
    }

    public string VolumeName { get; }

    public ObservableCollection<VolumeEntryViewModel> Entries { get; } = [];

    [ObservableProperty] private string _path = "/";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;

    /// <summary>Set when the engine stopped listing early; the UI says so rather than implying "that's all".</summary>
    [ObservableProperty] private bool _isTruncated;

    /// <summary>Empty is an ordinary state for a volume nothing has written to yet.</summary>
    public bool IsEmpty => !IsLoading && Error is null && Entries.Count == 0;

    public bool CanGoUp => Path != "/";

    partial void OnPathChanged(string value) => OnPropertyChanged(nameof(CanGoUp));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    private async Task LoadAsync(string path)
    {
        Error = null;
        IsLoading = true;
        try
        {
            var listing = await _engine.BrowseVolumeAsync(VolumeName, path);

            Entries.Clear();
            foreach (var entry in listing.Entries)
                Entries.Add(new VolumeEntryViewModel(entry));

            Path = listing.Path;
            IsTruncated = listing.Truncated;
        }
        catch (Exception ex)
        {
            // The mechanism can fail for reasons that have nothing to do with the volume — no image to
            // mount it into, for one — so the message matters more than usual here.
            Error = ex.Message;
            Entries.Clear();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task Open(VolumeEntryViewModel? entry)
    {
        if (entry is null || !entry.IsDirectory || IsLoading)
            return;

        await LoadAsync(Path == "/" ? "/" + entry.Name : Path + "/" + entry.Name);
    }

    [RelayCommand]
    private async Task GoUp()
    {
        if (!CanGoUp || IsLoading)
            return;

        var cut = Path.LastIndexOf('/');
        await LoadAsync(cut <= 0 ? "/" : Path[..cut]);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(Path);

    [RelayCommand]
    private void Close() => _onClose();
}
