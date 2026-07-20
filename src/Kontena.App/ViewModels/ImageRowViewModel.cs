using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

public sealed partial class ImageRowViewModel : ObservableObject
{
    private readonly ImageSummary _i;
    private readonly ImagesViewModel _parent;

    public ImageRowViewModel(ImageSummary image, ImagesViewModel parent)
    {
        _i = image;
        _parent = parent;
    }

    public string Id => _i.Id;

    public string RepoName => _i.Repository.Contains('/')
        ? _i.Repository[(_i.Repository.LastIndexOf('/') + 1)..]
        : _i.Repository;

    public string RepoNamespace => _i.Repository.Contains('/')
        ? _i.Repository[.._i.Repository.LastIndexOf('/')]
        : string.Empty;

    public string Tag => _i.Tag;

    public string ShortId => _i.Id.StartsWith("sha256:", StringComparison.Ordinal)
        ? _i.Id[7..19]
        : (_i.Id.Length > 12 ? _i.Id[..12] : _i.Id);

    public string SizeText => Format.Size(_i.SizeBytes);
    public string CreatedText => Format.Age(_i.CreatedAt);

    public bool InUse => _i.InUse;
    public string UseText => _i.InUse ? "In use" : "Unused";
    public IBrush UseBrush => new SolidColorBrush(Color.Parse(_i.InUse ? "#34D399" : "#5C6675"));

    [RelayCommand]
    private Task Delete() => _parent.DeleteAsync(Id);
}
