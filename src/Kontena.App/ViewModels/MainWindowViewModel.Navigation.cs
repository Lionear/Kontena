using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration.Models;
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
        Arrived(NavItems.FirstOrDefault(i => i.Key == key)?.Label ?? key, () => Navigate(key));

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
        // One unlabelled group: five entries do not need dividing, and a single heading over the whole
        // list says nothing. Deliberate rather than unfinished — see NavGroup.
        NavGroups.Clear();
        NavGroups.Add(Group(null,
            new NavItem("containers", "Containers", "IconContainer") { IsSelected = true },
            new NavItem("images", "Images", "IconLayers"),
            new NavItem("volumes", "Volumes", "IconDatabase"),
            new NavItem("networks", "Networks", "IconNetwork"),
            new NavItem("projects", "Projects", "IconBox")));
    }

    /// <summary>A section, with the shared navigate command already on every item in it.</summary>
    private NavGroup Group(string? label, params NavItem[] items)
    {
        var group = new NavGroup(label);

        foreach (var item in items)
        {
            item.Command = NavigateCommand;
            group.Items.Add(item);
        }

        return group;
    }
    /// <summary>The cluster (OAL) sidebar nav — the Kubernetes resource tree.</summary>
    private void SetClusterNav()
    {
        // Four sections, following the mockup's shape rather than its exact wording — it lists kinds
        // this app does not have pages for yet, and inventing headings for absent items would be a nav
        // that describes a different product.
        NavGroups.Clear();
        NavGroups.Add(Group("Cluster",
            new NavItem("overview", "Overview", "IconGauge") { IsSelected = true },
            new NavItem("nodes", "Nodes", "IconCpu"),
            new NavItem("namespaces", "Namespaces", "IconBox")));
        NavGroups.Add(Group("Workloads",
            new NavItem("workloads", "All workloads", "IconLayers"),
            new NavItem("pods", "Pods", "IconContainer")));
        NavGroups.Add(Group("Network",
            new NavItem("services", "Services", "IconNetwork"),
            new NavItem("ingresses", "Ingresses", "IconGlobe"),
            new NavItem("portforwards", "Port forwards", "IconPlug")));
        NavGroups.Add(Group("Storage",
            new NavItem("pvcs", "Volume claims", "IconDatabase")));
        NavGroups.Add(Group("System",
            new NavItem("resources", "Resources", "IconBox"),
            new NavItem("apply", "Apply manifest", "IconPlay"),
            new NavItem("terminal", "Terminal", "IconTerminal")));
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

        // Nodes/Namespaces are cluster-wide; the rest honour the namespace picker.
        CurrentPage = key switch
        {
            "overview" => new ClusterOverviewViewModel(_cluster),
            // RequestConfirm because the metrics-server install writes to the cluster and asks first
            // (KON-93); the other cluster pages route their confirms through the shell callbacks they
            // are handed.
            "nodes" => new ClusterNodesViewModel(_cluster) { RequestConfirm = ShowConfirm },
            "namespaces" => new ClusterNamespacesViewModel(_cluster),
            _ when WorkloadNavGroups.KindOf(key) is { } kind =>
                new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload, ShowWorkloadDetail, kind),
            // The dashboard only where there is something to summarise. With one kind the sidebar has
            // no submenu either, and a dashboard of a single card is a page that says less than the
            // list it replaces — so there it stays the list (KON-174).
            "workloads" when WorkloadNavGroups.ShouldGroup(_workloadGroups) =>
                new ClusterWorkloadsDashboardViewModel(
                    _cluster, ActiveNamespace,
                    onOpenKind: kind => NavigateCluster(WorkloadNavGroups.KeyFor(kind)),
                    onOpenWorkload: ShowWorkloadDetail),
            "workloads" => new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload, ShowWorkloadDetail),
            "pods" => new ClusterPodsViewModel(_cluster, ActiveNamespace, ShowPodDetail, ConfirmDeletePod),
            "services" => new ClusterServicesViewModel(_cluster, ActiveNamespace, ShowServicePortForward, ShowServiceDetail),
            "ingresses" => new ClusterIngressesViewModel(_cluster, ActiveNamespace),
            "pvcs" => new ClusterPvcsViewModel(_cluster, ActiveNamespace),
            "portforwards" => new PortForwardsViewModel(_portForwards),
            // Any kind the cluster serves, custom ones included (KON-75). RequestConfirm
            // because deleting from here is as destructive as anywhere else.
            "resources" => new ClusterResourcesViewModel(_cluster, ActiveNamespace) { RequestConfirm = ShowConfirm },
            // A shell on this machine, already on this cluster (KON-171). Falls back to the
            // overview when the active backend is not a kubeconfig context, so the page can never
            // open onto a cluster it cannot name.
            "terminal" when CreateClusterTerminals() is { } terminals => terminals,
            "apply" => new ApplyManifestViewModel(_cluster, EngineName, onApplied: () =>
            {
                // An apply can create or remove anything — refresh the counts, not the open page.
                _ = UpdateClusterNavCountsAsync();
                return Task.CompletedTask;
            }, ActiveNamespace),
            _ => new ClusterOverviewViewModel(_cluster),
        };

        // The search term does not survive navigating away, and that is the honest behaviour while
        // cluster pages are rebuilt on every visit: the page it filtered no longer exists. The engine
        // pages keep theirs because they are long-lived fields. Restoring a term onto a fresh page
        // would show a filtered list with no way to tell it had been filtered (KON-164).
        SearchText = string.Empty;
    }
    /// <summary>Rebuild the currently-selected cluster page (e.g. after an action mutates it).</summary>
    private void ReloadCurrentClusterPage()
    {
        if (!IsClusterMode)
            return;

        _ = NavigateClusterAfterCountsAsync(_clusterPageKey);
    }

    /// <summary>
    /// Count first, then build the page (KON-200).
    /// <para>
    /// Which page Workloads is — the dashboard or the plain list — depends on how many kinds exist,
    /// and that answer arrives with the counts. Navigating first meant deciding on the namespace you
    /// had just left: one kind to several gave the list, several to one gave the dashboard. Both
    /// directions were reported. The same order applies after an apply, which can add the first
    /// DaemonSet or remove the last.
    /// </para>
    /// <para>
    /// The counts failing must not cost the navigation — a page built from a stale count is still
    /// better than no page at all — so the await is guarded and the key resolved either way.
    /// </para>
    /// </summary>
    private async Task NavigateClusterAfterCountsAsync(string key)
    {
        try
        {
            await UpdateClusterNavCountsAsync();
        }
        catch (Exception)
        {
            // Unreachable cluster, a call that timed out: the page itself reports that far better
            // than a nav that never happens.
        }

        if (IsClusterMode)
            NavigateCluster(WorkloadNavGroups.ResolveKey(key, _workloadGroups));
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
        SetNavCount("ingresses", (await _cluster.ListIngressesAsync(ns)).Count.ToString(ci));
        SetNavCount("pvcs", (await _cluster.ListPvcsAsync(ns)).Count.ToString(ci));
        UpdatePortForwardCount();
    }
    /// <summary>Which cluster page is open, including a per-kind workloads page.</summary>
    private string _clusterPageKey = "overview";

    /// <summary>The workload kinds the last count found, which is what decides the Workloads page.</summary>
    private IReadOnlyList<WorkloadNavGroups.Group> _workloadGroups = [];


    /// <summary>
    /// Rebuild the per-kind sub-entries under Workloads (KON-169). Which entries and in what order is
    /// <see cref="WorkloadNavGroups"/>; this only reconciles the nav collection with that answer.
    /// </summary>
    private void SyncWorkloadKindNav(IReadOnlyList<Workload> workloads)
    {
        // Within the group that holds Workloads, not the whole sidebar: the children belong to their
        // parent, and inserting by an index into a flat list was only ever a way of saying that.
        var items = NavGroups.FirstOrDefault(g => g.Items.Any(i => i.Key == "workloads"))?.Items;
        if (items is null)
            return;

        var parentIndex = items.ToList().FindIndex(i => i.Key == "workloads");

        // Drop the current children before rebuilding; the set changes as objects come and go.
        for (var i = items.Count - 1; i > parentIndex; i--)
        {
            if (items[i].IsChild)
                items.RemoveAt(i);
        }

        var groups = WorkloadNavGroups.For(workloads);
        _workloadGroups = groups;

        // Always listed, never folded (KON-219). The kinds used to hide behind a chevron on the entry
        // above them, which repeated the word the group heading already carries and gave every kind
        // page two routes: this submenu and the dashboard, which does the same job with counts and
        // rollout state. One kind is not a set worth listing — the entry above it already is the page.
        if (!WorkloadNavGroups.ShouldGroup(groups))
            return;

        var at = parentIndex + 1;
        foreach (var group in groups)
        {
            var key = WorkloadNavGroups.KeyFor(group.Kind);

            items.Insert(at++, new NavItem(key, WorkloadNavGroups.LabelFor(group.Kind), "IconLayers", isChild: true)
            {
                Count = group.Count.ToString(CultureInfo.InvariantCulture),
                Command = NavigateCommand,
                IsSelected = _clusterPageKey == key,
            });
        }
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

        Arrived("Activity", ShowActivity);
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
    /// <summary>
    /// The terminal font as it is right now. Read from the store rather than the cached settings so a
    /// font changed in Settings applies to the next terminal opened, not the next restart.
    /// </summary>
    private TerminalFont CurrentTerminalFont()
    {
        var current = _store.Load();
        return new TerminalFont(current.TerminalFontFamily, current.TerminalFontSize, current.TerminalLigatures);
    }

    private void ShowContainerDetail(ContainerSummary summary)
    {
        if (_engine is null)
            return;

        Arrived($"container {summary.Name}", () => ShowContainerDetail(summary), summary);
        DisposeDetail();

        var font = CurrentTerminalFont();

        _detail = new ContainerDetailViewModel(_engine, summary, GoBack, font)
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

        Arrived("Settings", ShowSettings);
        CurrentPage = SettingsPage;
        SearchText = string.Empty;
        foreach (var item in NavItems)
            item.IsSelected = false;
    }
    [RelayCommand]
    private void ShowAbout()
    {
        Arrived("About", ShowAbout);
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

        // By key, like the cluster side already did. The comment on SetNavCount warned about exactly
        // this: an index-based assignment puts the image count on Volumes the day the nav gains an
        // entry — and grouping the nav is that day.
        var ci = CultureInfo.InvariantCulture;
        SetNavCount("containers", Containers.ContainerCount.ToString(ci));
        SetNavCount("images", (await _engine.ListImagesAsync()).Count.ToString(ci));
        SetNavCount("volumes", (await _engine.ListVolumesAsync()).Count.ToString(ci));
        SetNavCount("networks", (await _engine.ListNetworksAsync()).Count.ToString(ci));

        var projects = (await _engine.ListContainersAsync())
            .Where(c => c.Labels.ContainsKey(ComposeProjectsViewModel.ProjectLabel))
            .Select(c => c.Labels[ComposeProjectsViewModel.ProjectLabel])
            .Distinct()
            .Count();
        SetNavCount("projects", projects.ToString(ci));
    }
}
