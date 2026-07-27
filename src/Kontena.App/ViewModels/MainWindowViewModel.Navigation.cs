using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Which page is on screen: the sidebar's items and their counts, the pages that have no
/// dialog of their own, and the theme toggle that sits in the same bar.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Open the Projects page at one project (KON-159). The Containers grid shows a stack; this is
    /// where it is operated as a whole, and the group row is the way across rather than a second copy
    /// of the same controls.
    /// </summary>
    private void ShowProject(string project)
    {
        if (ComposeProjects is null)
            return;

        Navigate("projects");
        ComposeProjects.SearchText = project;
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        if (IsClusterMode)
        {
            NavigateCluster(key);
            return;
        }

        IListPage? page = key switch
        {
            "images" => Images,
            "volumes" => Volumes,
            "networks" => Networks,
            "projects" => ComposeProjects,
            "containers" => Containers,
            _ => Containers,
        };
        if (page is null)
            return;

        DisposeDetail();
        CurrentPage = page;
        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;

        SearchText = page.SearchText;

        if (!page.HasLoaded)
            _ = page.LoadAsync();
    }
    /// <summary>The engine (CEAL) sidebar nav — Containers/Images/Volumes/Networks/Projects.</summary>
    private void SetEngineNav()
    {
        NavItems.Clear();
        NavItems.Add(new NavItem("containers", "Containers", "IconContainer") { IsSelected = true });
        NavItems.Add(new NavItem("images", "Images", "IconLayers"));
        NavItems.Add(new NavItem("volumes", "Volumes", "IconDatabase"));
        NavItems.Add(new NavItem("networks", "Networks", "IconNetwork"));
        NavItems.Add(new NavItem("projects", "Projects", "IconBox"));
        foreach (var item in NavItems)
            item.Command = NavigateCommand;
    }
    /// <summary>The cluster (OAL) sidebar nav — the Kubernetes resource tree.</summary>
    private void SetClusterNav()
    {
        NavItems.Clear();
        NavItems.Add(new NavItem("overview", "Overview", "IconGauge") { IsSelected = true });
        NavItems.Add(new NavItem("nodes", "Nodes", "IconCpu"));
        NavItems.Add(new NavItem("namespaces", "Namespaces", "IconBox"));
        NavItems.Add(new NavItem("workloads", "Workloads", "IconLayers"));
        NavItems.Add(new NavItem("pods", "Pods", "IconContainer"));
        NavItems.Add(new NavItem("services", "Services", "IconNetwork"));
        NavItems.Add(new NavItem("portforwards", "Port forwards", "IconPlug"));
        NavItems.Add(new NavItem("apply", "Apply manifest", "IconPlay"));
        foreach (var item in NavItems)
            item.Command = NavigateCommand;
    }
    private void NavigateCluster(string key)
    {
        if (_cluster is null)
            return;

        DisposeDetail();
        (CurrentPage as PortForwardsViewModel)?.Dispose();
        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;

        // Nodes/Namespaces are cluster-wide; the rest honour the namespace picker.
        CurrentPage = key switch
        {
            "overview" => new ClusterOverviewViewModel(_cluster),
            "nodes" => new ClusterNodesViewModel(_cluster),
            "namespaces" => new ClusterNamespacesViewModel(_cluster),
            "workloads" => new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload),
            "pods" => new ClusterPodsViewModel(_cluster, ActiveNamespace, ShowPodDetail, ConfirmDeletePod),
            "services" => new ClusterServicesViewModel(_cluster, ActiveNamespace, ShowServicePortForward),
            "portforwards" => new PortForwardsViewModel(_portForwards),
            "apply" => new ApplyManifestViewModel(_cluster, EngineName, onApplied: () =>
            {
                // An apply can create or remove anything — refresh the counts, not the open page.
                _ = UpdateClusterNavCountsAsync();
                return Task.CompletedTask;
            }, ActiveNamespace),
            _ => new ClusterOverviewViewModel(_cluster),
        };
        SearchText = string.Empty;
    }
    /// <summary>Rebuild the currently-selected cluster page (e.g. after an action mutates it).</summary>
    private void ReloadCurrentClusterPage()
    {
        if (!IsClusterMode)
            return;

        var key = NavItems.FirstOrDefault(i => i.IsSelected)?.Key ?? "overview";
        NavigateCluster(key);
        _ = UpdateClusterNavCountsAsync();
    }
    private async Task UpdateClusterNavCountsAsync()
    {
        if (_cluster is null)
            return;

        var ci = CultureInfo.InvariantCulture;
        var ns = SelectedNamespace == AllNamespaces ? null : SelectedNamespace;
        SetNavCount("nodes", (await _cluster.ListNodesAsync()).Count.ToString(ci));
        SetNavCount("namespaces", (await _cluster.ListNamespacesAsync()).Count.ToString(ci));
        SetNavCount("workloads", (await _cluster.ListWorkloadsAsync(null, ns)).Count.ToString(ci));
        SetNavCount("pods", (await _cluster.ListPodsAsync(ns)).Count.ToString(ci));
        SetNavCount("services", (await _cluster.ListServicesAsync(ns)).Count.ToString(ci));
        UpdatePortForwardCount();
    }
    // Keyed rather than indexed: the nav gained an entry in the middle once already, and an index-based
    // assignment puts the pod count on Services the day it gains another.
    private void SetNavCount(string key, string count)
    {
        if (NavItems.FirstOrDefault(i => i.Key == key) is { } item)
            item.Count = count;
    }
    [RelayCommand]
    private void ShowActivity()
    {
        if (Activity is null)
            return;

        DisposeDetail();
        CurrentPage = Activity;
        SearchText = Activity.SearchText;
        foreach (var item in NavItems)
            item.IsSelected = false;
    }
    [RelayCommand]
    private async Task RefreshCurrentPageAsync()
    {
        if (CurrentPage is IListPage page)
            await page.LoadAsync();
    }
    private void ShowContainerDetail(ContainerSummary summary)
    {
        if (_engine is null)
            return;

        DisposeDetail();

        // Reload settings so a just-changed terminal font is picked up.
        var current = _store.Load();
        var font = new TerminalFont(current.TerminalFontFamily, current.TerminalFontSize, current.TerminalLigatures);

        _detail = new ContainerDetailViewModel(_engine, summary, ShowContainers, font)
        {
            RequestConfirm = ShowConfirm,
        };
        CurrentPage = _detail;
    }
    private void ShowContainers()
    {
        DisposeDetail();
        if (Containers is null)
            return;

        CurrentPage = Containers;
        SearchText = Containers.SearchText;
    }
    [RelayCommand]
    private void ShowSettings()
    {
        DisposeDetail();
        CloseDialog();
        if (SettingsPage is null)
            return;

        CurrentPage = SettingsPage;
        SearchText = string.Empty;
        foreach (var item in NavItems)
            item.IsSelected = false;
    }
    [RelayCommand]
    private void ShowAbout()
    {
        DisposeDetail();
        CloseDialog();
        CurrentPage = About;
        SearchText = string.Empty;
        foreach (var item in NavItems)
            item.IsSelected = false;
    }
    // ── Theme quick-toggle (topbar) ─────────────────────────────────────────

    /// <summary>Resource key of the icon shown on the topbar theme button.</summary>
    [ObservableProperty] private string _themeToggleIconKey = "IconMoon";

    /// <summary>Tooltip for the topbar theme button.</summary>
    [ObservableProperty] private string _themeToggleTip = "Toggle theme";

    /// <summary>Flips between Light and Dark based on what is actually on screen.
    /// The three-way preference (incl. System) still lives in Settings.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            ? ThemePreference.Light
            : ThemePreference.Dark;

        if (SettingsPage is not null)
        {
            _settings = _settings with { Theme = next };
            SettingsPage.Theme = next; // applies + persists via its own handler
        }
        else
        {
            ThemeApplier.Apply(next);
            _settings = _store.Update(s => s with { Theme = next });
        }

        SyncThemeToggleIcon();
    }
    private void SyncThemeToggleIcon()
    {
        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        ThemeToggleIconKey = isDark ? "IconSun" : "IconMoon";
        ThemeToggleTip = isDark ? "Switch to light theme" : "Switch to dark theme";
    }
    private async Task UpdateNavCountsAsync()
    {
        if (_engine is null || Containers is null)
            return;

        var ci = CultureInfo.InvariantCulture;
        NavItems[0].Count = Containers.ContainerCount.ToString(ci);
        NavItems[1].Count = (await _engine.ListImagesAsync()).Count.ToString(ci);
        NavItems[2].Count = (await _engine.ListVolumesAsync()).Count.ToString(ci);
        NavItems[3].Count = (await _engine.ListNetworksAsync()).Count.ToString(ci);

        var projects = (await _engine.ListContainersAsync())
            .Where(c => c.Labels.ContainsKey(ComposeProjectsViewModel.ProjectLabel))
            .Select(c => c.Labels[ComposeProjectsViewModel.ProjectLabel])
            .Distinct()
            .Count();
        NavItems[4].Count = projects.ToString(ci);
    }
}
