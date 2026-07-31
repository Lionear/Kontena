using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class AddBackendView : UserControl
{
    public AddBackendView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Picks the folder holding <c>ca.pem</c>, <c>cert.pem</c> and <c>key.pem</c>. A picker rather than
    /// typing only: this path is the difference between a verified server and any server that answers.
    /// </summary>
    private async void OnBrowseCertificates(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddBackendViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Certificate directory",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
            vm.CertificateDirectory = folders[0].Path.LocalPath;
    }

    /// <summary>Picks the private key to authenticate with (KON-261).</summary>
    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddBackendViewModel vm)
            return;

        if (await SshKeyPicker.PickAsync(TopLevel.GetTopLevel(this)) is { } path)
            vm.KeyFile = path;
    }

    private async void OnBrowseKubeconfig(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddBackendViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Kubeconfig",
            AllowMultiple = false,

            // A kubeconfig has no required extension — plenty are called "config" or end in .yaml — so
            // the YAML filter is an aid, not a gate.
            FileTypeFilter =
            [
                new FilePickerFileType("Kubeconfig") { Patterns = ["config", "*.yaml", "*.yml", "*.conf"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0)
            vm.KubeconfigPath = files[0].Path.LocalPath;
    }
}
