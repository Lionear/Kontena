using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Models;

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
            // A parent with children is a group header, not a page: clicking it opens the group rather
            // than loading every kind at once (KON-169). With no children there is no group to open, so
            // it stays an ordinary page — otherwise a cluster running one kind could not reach its list
            // at all.
            if (NavItems.FirstOrDefault(i => i.Key == key) is { HasChildren: true })
            {
                ToggleNavGroup(key);
                return;
            }

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

        // Remembered separately from the nav items because the per-kind children are rebuilt as
        // workloads come and go (KON-169); IsSelected on an item that gets replaced is not a record
        // of where the user is.
        _clusterPageKey = key;

        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;

        // Opening a child keeps its parent open, otherwise the group folds up under the page you just
        // navigated into and the trail back to its siblings disappears. The refresh matters as much as
        // the flag: the children are only inserted while rebuilding, so setting IsExpanded on its own
        // leaves an open chevron above nothing.
        if (WorkloadNavGroups.KindOf(key) is not null
            && NavItems.FirstOrDefault(i => i.Key == "workloads") is { IsExpanded: false } workloadsNav)
        {
            workloadsNav.IsExpanded = true;
            _ = UpdateClusterNavCountsAsync();
        }

        // Nodes/Namespaces are cluster-wide; the rest honour the namespace picker.
        CurrentPage = key switch
        {
            "overview" => new ClusterOverviewViewModel(_cluster),
            "nodes" => new ClusterNodesViewModel(_cluster),
            "namespaces" => new ClusterNamespacesViewModel(_cluster),
            _ when WorkloadNavGroups.KindOf(key) is { } kind =>
                new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload, ShowWorkloadDetail, kind),
            "workloads" => new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload, ShowWorkloadDetail),
            "pods" => new ClusterPodsViewModel(_cluster, ActiveNamespace, p => ShowPodDetail(p), ConfirmDeletePod),
            "services" => new ClusterServicesViewModel(_cluster, ActiveNamespace, ShowServicePortForward, ShowServiceDetail),
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

        NavigateCluster(_clusterPageKey);
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

        // One call, grouped here, rather than one per kind: five round-trips to fill five badges is
        // five chances for them to disagree with each other and with the list they label (KON-169).
        var workloads = await _cluster.ListWorkloadsAsync(null, ns);
        SetNavCount("workloads", workloads.Count.ToString(ci));
        SyncWorkloadKindNav(workloads);

        SetNavCount("pods", (await _cluster.ListPodsAsync(ns)).Count.ToString(ci));
        SetNavCount("services", (await _cluster.ListServicesAsync(ns)).Count.ToString(ci));
        UpdatePortForwardCount();
    }
    /// <summary>Which cluster page is open, including a per-kind workloads page.</summary>
    private string _clusterPageKey = "overview";


    /// <summary>
    /// Rebuild the per-kind sub-entries under Workloads (KON-169). Which entries and in what order is
    /// <see cref="WorkloadNavGroups"/>; this only reconciles the nav collection with that answer.
    /// </summary>
    private void SyncWorkloadKindNav(IReadOnlyList<Workload> workloads)
    {
        var parentIndex = NavItems.ToList().FindIndex(i => i.Key == "workloads");
        if (parentIndex < 0)
            return;

        var parent = NavItems[parentIndex];

        // Drop the current children before rebuilding; the set changes as objects come and go.
        for (var i = NavItems.Count - 1; i > parentIndex; i--)
        {
            if (NavItems[i].IsChild)
                NavItems.RemoveAt(i);
        }

        var groups = WorkloadNavGroups.For(workloads);
        parent.HasChildren = WorkloadNavGroups.ShouldGroup(groups);

        if (!parent.HasChildren)
        {
            parent.IsExpanded = false;
            return;
        }

        if (!parent.IsExpanded)
            return;

        var at = parentIndex + 1;
        foreach (var group in groups)
        {
            var key = WorkloadNavGroups.KeyFor(group.Kind);

            NavItems.Insert(at++, new NavItem(key, WorkloadNavGroups.LabelFor(group.Kind), "IconLayers", isChild: true)
            {
                Count = group.Count.ToString(CultureInfo.InvariantCulture),
                Command = NavigateCommand,
                IsSelected = _clusterPageKey == key,
            });
        }
    }

    /// <summary>Expand or collapse the Workloads sub-entries without navigating (KON-169).</summary>
    [RelayCommand]
    private void ToggleNavGroup(string key)
    {
        if (NavItems.FirstOrDefault(i => i.Key == key) is not { HasChildren: true } item)
            return;

        item.IsExpanded = !item.IsExpanded;
        _ = UpdateClusterNavCountsAsync();
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
