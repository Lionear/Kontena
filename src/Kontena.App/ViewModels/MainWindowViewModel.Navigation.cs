using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Engines.Plugins;
using Kontena.Sdk;
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

        // Before the mode switch: a plugin page belongs to neither nav, and both of the switches below
        // fall through to a page of their own on an unknown key.
        if (_pluginPages.TryGetValue(key, out var pluginPage))
        {
            ShowPluginPage(key, pluginPage);
            return;
        }

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

        CloseDetail();
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

        AddPluginNav();
    }

    /// <summary>Nav key to the page behind it, for everything a plugin contributed (KON-331).</summary>
    private readonly Dictionary<string, PluginPage> _pluginPages = new(StringComparer.Ordinal);

    /// <summary>
    /// Appends what the loaded plugins contribute to the nav that was just built (KON-331).
    /// <para>
    /// Its own group, at the end, in both navs: a plugin page is not part of the engine's story or the
    /// cluster's, and where it would otherwise sit is a question with no answer that stays true for the
    /// next plugin.
    /// </para>
    /// </summary>
    private void AddPluginNav()
    {
        _pluginPages.Clear();

        var items = new List<NavItem>();

        foreach (var plugin in _plugins.Where(p => p.Status == PluginStatus.Loaded && p.Manifest is not null))
        {
            foreach (var page in plugin.Pages)
            {
                // Prefixed with the plugin id: two plugins naming a page "editor" would otherwise be one
                // entry, and the second would open the first's page.
                var key = $"plugin:{plugin.Manifest!.Id}:{page.Key}";
                _pluginPages[key] = page;
                items.Add(new NavItem(key, page.Label, page.IconKey)
                {
                    IsPlugin = true,
                    PluginTip = $"From the plugin {plugin.Manifest.Name} {plugin.Manifest.Version}",
                });
            }
        }

        if (items.Count > 0)
            NavGroups.Add(Group("Plugins", [.. items]));
    }

    /// <summary>
    /// Opens a page a plugin contributed, building its control inside the host's containment (KON-331).
    /// The loader already keeps a plugin that cannot load out of the start; this is the same promise for
    /// one that cannot draw — it costs its own content area, not the window around it.
    /// </summary>
    private void ShowPluginPage(string key, PluginPage page)
    {
        CloseDetail();

        try
        {
            CurrentPage = page.CreateView();
        }
        catch (Exception ex)
        {
            CurrentPage = new TextBlock
            {
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
                Text = $"{page.Label} could not be opened: {ex.Message}",
            };
        }

        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;
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
            new NavItem("pvcs", "Volume claims", "IconDatabase"),
            new NavItem("volumes", "Volumes", "IconLayers"),
            new NavItem("storageclasses", "Storage classes", "IconTag")));
        NavGroups.Add(Group("Config",
            new NavItem("configmaps", "Config maps", "IconFolder"),
            new NavItem("secrets", "Secrets", "IconHash")));
        NavGroups.Add(Group("System",
            new NavItem("events", "Events", "IconActivity"),
            new NavItem("resources", "Resources", "IconBox"),
            new NavItem("apply", "Apply manifest", "IconPlay"),
            new NavItem("terminal", "Terminal", "IconTerminal")));

        AddPluginNav();
    }
    /// <param name="refreshCounts">
    /// False only where the caller has just counted, so the badges are not refetched twice for one
    /// navigation.
    /// </param>
    private void NavigateCluster(string key, bool refreshCounts = true)
    {
        if (_cluster is null)
            return;

        CloseDetail();
        // Any page that holds something running — a port-forward registry, a watch stream (KON-250).
        // By interface rather than by type: the list of page types that own a resource has grown
        // twice now, and naming them one at a time is how the third one gets missed.
        (CurrentPage as IDisposable)?.Dispose();

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
            "nodes" => new ClusterNodesViewModel(_cluster, ShowDrainNode, ShowNodeDetail) { RequestConfirm = ShowConfirm },
            "namespaces" => new ClusterNamespacesViewModel(_cluster, ShowNamespaceDetail),
            // RequestConfirm because the page owns its own delete, and its confirm is the only thing
            // between a click and a workload that is gone (KON-332).
            _ when WorkloadNavGroups.KindOf(key) is { } kind =>
                new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload, ShowWorkloadDetail, kind)
                { RequestConfirm = ShowConfirm },
            // The dashboard only where there is something to summarise. With one kind the sidebar has
            // no submenu either, and a dashboard of a single card is a page that says less than the
            // list it replaces — so there it stays the list (KON-174).
            "workloads" when WorkloadNavGroups.ShouldGroup(_workloadGroups) =>
                new ClusterWorkloadsDashboardViewModel(
                    _cluster, ActiveNamespace,
                    onOpenKind: kind => NavigateCluster(WorkloadNavGroups.KeyFor(kind)),
                    onOpenWorkload: ShowWorkloadDetail,
                    onOpenPods: () => NavigateCluster("pods")),
            "workloads" => new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload, ShowWorkloadDetail)
                { RequestConfirm = ShowConfirm },
            "pods" => new ClusterPodsViewModel(_cluster, ActiveNamespace, ShowPodDetail, ConfirmDeletePod),
            "services" => new ClusterServicesViewModel(_cluster, ActiveNamespace, ShowServicePortForward, ShowServiceDetail)
                { RequestConfirm = ShowConfirm },
            "ingresses" => new ClusterIngressesViewModel(_cluster, ActiveNamespace) { RequestConfirm = ShowConfirm },
            // The three storage pages point at each other: a claim to its volume and its class, a
            // volume back to its claim (KON-254). Routing by search term rather than by a filter the
            // page owns keeps one way of saying "show me this one".
            "pvcs" => new ClusterPvcsViewModel(
                _cluster, ActiveNamespace,
                onOpenVolume: name => OpenStorage("volumes", name),
                onOpenClass: name => OpenStorage("storageclasses", name)),
            "volumes" => new ClusterVolumesViewModel(
                _cluster,
                onOpenClaim: name => OpenStorage("pvcs", name),
                onOpenClass: name => OpenStorage("storageclasses", name)),
            "storageclasses" => new ClusterStorageClassesViewModel(_cluster),
            "portforwards" => new PortForwardsViewModel(_portForwards),
            // RequestConfirm because deleting one is as destructive here as anywhere else (KON-253).
            "configmaps" => new ClusterConfigMapsViewModel(_cluster, ActiveNamespace)
            {
                RequestConfirm = ShowConfirm, RequestEdit = ShowManifestEditor,
                RequestOpenDetail = ShowConfigDetail,
            },
            // Keys and sizes; a value only moves when asked for, one key at a time (KON-249).
            "secrets" => new ClusterSecretsViewModel(_cluster, ActiveNamespace)
            {
                RequestConfirm = ShowConfirm, RequestEdit = ShowManifestEditor,
                RequestOpenDetail = ShowConfigDetail,
            },
            // The feed you open when you do not yet know which object is the broken one (KON-248).
            "events" => new ClusterEventsViewModel(_cluster, ActiveNamespace, OpenEventObjectAsync),
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

        // The counts follow the same event the page just reloaded on (KON-339). Set here rather than
        // on each of the constructors above: one place that knows a page is on screen, and the watch
        // having already started in the constructor costs nothing — the callback is read when it
        // fires, not when the stream opens.
        if (CurrentPage is IClusterLivePage live)
            live.Changed = () => _ = RefreshClusterNavCountsAsync();

        // Only the open page's stream drives that callback, so the badges stop following the moment
        // you land somewhere that watches nothing — the Workloads dashboard, Config maps, Events. Seen
        // for real: a pod deleted while its page was open refreshed the counts mid-termination, the
        // page was navigated away from before the pod actually went, and the badge kept the number it
        // had caught in between. After the page rather than before it, and not awaited: a count is
        // worth a round-trip but never worth making the click wait for one.
        if (refreshCounts)
            _ = RefreshClusterNavCountsAsync();

        // The search term does not survive navigating away, and that is the honest behaviour while
        // cluster pages are rebuilt on every visit: the page it filtered no longer exists. The engine
        // pages keep theirs because they are long-lived fields. Restoring a term onto a fresh page
        // would show a filtered list with no way to tell it had been filtered (KON-164).
        SearchText = string.Empty;
    }
    /// <summary>
    /// Refresh the badges after the open page saw the cluster change (KON-339). Failure is silent on
    /// purpose: this runs off a watch stream nobody asked to be told about, and a count that could not
    /// be refetched is a number that stays as it was — the page itself reports an unreachable cluster.
    /// </summary>
    private async Task RefreshClusterNavCountsAsync()
    {
        try
        {
            await UpdateClusterNavCountsAsync();
        }
        catch (Exception)
        {
            // Left as they were, which is the same answer a refresh that never ran would give.
        }
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
            NavigateCluster(WorkloadNavGroups.ResolveKey(key, _workloadGroups), refreshCounts: false);
    }
    /// <summary>
    /// Fill the sidebar badges. Every lister is started before any of them is awaited (KON-338):
    /// twelve badges that know nothing of each other were costing twelve round-trips one behind the
    /// other, and since this runs before the page is built, it is what a namespace switch waits on.
    /// The adapter already fetches its five workload kinds exactly this way.
    /// </summary>
    private async Task UpdateClusterNavCountsAsync()
    {
        if (_cluster is null)
            return;

        var ci = CultureInfo.InvariantCulture;
        var ns = SelectedNamespace == AllNamespaces ? null : SelectedNamespace;

        var nodes = _cluster.ListNodesAsync().AsTask();
        var namespaces = _cluster.ListNamespacesAsync().AsTask();

        // One call, grouped here, rather than one per kind: five round-trips to fill five badges is
        // five chances for them to disagree with each other and with the list they label (KON-169).
        var workloads = _cluster.ListWorkloadsAsync(null, ns).AsTask();

        var pods = _cluster.ListPodsAsync(ns).AsTask();
        var services = _cluster.ListServicesAsync(ns).AsTask();
        var configMaps = _cluster.ListConfigMapsAsync(ns).AsTask();
        var secrets = _cluster.ListSecretsAsync(ns).AsTask();
        var events = _cluster.ListEventsAsync(ns).AsTask();
        var ingresses = _cluster.ListIngressesAsync(ns).AsTask();
        var pvcs = _cluster.ListPvcsAsync(ns).AsTask();
        var volumes = _cluster.ListVolumesAsync().AsTask();
        var storageClasses = _cluster.ListStorageClassesAsync().AsTask();

        await Task.WhenAll(
            nodes, namespaces, workloads, pods, services, configMaps,
            secrets, events, ingresses, pvcs, volumes, storageClasses);

        SetNavCount("nodes", nodes.Result.Count.ToString(ci));
        SetNavCount("namespaces", namespaces.Result.Count.ToString(ci));
        SyncNamespacePicker(namespaces.Result);

        SetNavCount("workloads", workloads.Result.Count.ToString(ci));
        SyncWorkloadKindNav(workloads.Result);

        SetNavCount("pods", pods.Result.Count.ToString(ci));
        SetNavCount("services", services.Result.Count.ToString(ci));
        SetNavCount("configmaps", configMaps.Result.Count.ToString(ci));
        SetNavCount("secrets", secrets.Result.Count.ToString(ci));

        // Warnings, not events (KON-248). Every namespace has events all the time, so a total is a
        // badge that is always lit and therefore says nothing; the count of warnings is the one number
        // worth carrying into the sidebar, and no warnings means no badge at all.
        var warnings = events.Result.Count(e => e.Severity == EventSeverity.Warning);
        SetNavCount("events", warnings > 0 ? warnings.ToString(ci) : string.Empty);

        SetNavCount("ingresses", ingresses.Result.Count.ToString(ci));
        SetNavCount("pvcs", pvcs.Result.Count.ToString(ci));
        SetNavCount("volumes", volumes.Result.Count.ToString(ci));
        SetNavCount("storageclasses", storageClasses.Result.Count.ToString(ci));
        UpdatePortForwardCount();
    }
    /// <summary>
    /// Keep the namespace picker in step with the cluster (KON-343).
    /// <para>
    /// It was filled once when the cluster opened and never again, so a namespace created afterwards
    /// could not be picked — while the list beside it was already showing that namespace's contents.
    /// Two pieces of UI over one cluster, disagreeing. The names are in hand here anyway: only the
    /// count was being kept, so this costs no call of its own.
    /// </para>
    /// <para>
    /// Reconciled rather than rebuilt. Clearing the collection drops the ComboBox's selection, and
    /// this runs on every watch event of the open page — the picker would reset itself while you
    /// were reading the list it filters.
    /// </para>
    /// </summary>
    private void SyncNamespacePicker(IReadOnlyList<KubeNamespace> namespaces)
    {
        ListSync.Apply(Namespaces, [AllNamespaces, .. namespaces.Select(n => n.Name)]);

        // The selected namespace can be deleted out from under the picker. "All namespaces" is the
        // one entry that is always there, and it shows the deletion rather than hiding it: an empty
        // picker with a stale filter still applied would be the worst of the three outcomes.
        if (SelectedNamespace is not null && !Namespaces.Contains(SelectedNamespace))
            SelectedNamespace = AllNamespaces;
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

        MovePodsUnderDeployments(items);
    }

    /// <summary>
    /// Put Pods directly under Deployments (Rick, 2026-08-03). The pods you go looking for are nearly
    /// always a Deployment's, and Pods sat at the foot of the kinds with everything else between them.
    /// <para>
    /// With no Deployments in this namespace it stays where it was, at the end: the entry it belongs
    /// under is not there to belong under. Redone on every rebuild rather than fixed at construction,
    /// because the kind above it comes and goes with the namespace.
    /// </para>
    /// </summary>
    private static void MovePodsUnderDeployments(ObservableCollection<NavItem> items)
    {
        var pods = items.FirstOrDefault(i => i.Key == "pods");
        var deployments = items.FirstOrDefault(i => i.Key == WorkloadNavGroups.KeyFor(WorkloadKind.Deployment));
        if (pods is null || deployments is null)
            return;

        var to = items.IndexOf(deployments) + 1;
        var from = items.IndexOf(pods);

        // A rebuild leaves Pods at the end, so it only ever slides up. Guarding on the whole range
        // rather than on equality keeps that assumption from being load-bearing.
        if (from <= to)
            return;

        // Move rather than remove-and-insert: the sidebar is bound to this collection, and a Move is
        // one row sliding where a Remove plus an Add is a row that blinks out and comes back.
        items.Move(from, to);
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
        CloseDetail();
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

        var font = CurrentTerminalFont();

        var detail = new ContainerDetailViewModel(_engine, summary, font)
        {
            RequestConfirm = ShowConfirm,
        };

        ShowDetail(detail, $"container {summary.Name}", summary);
    }
    private void ShowContainers()
    {
        CloseDetail();
        if (Containers is null)
            return;

        CurrentPage = Containers;
        SearchText = Containers.SearchText;
    }
    [RelayCommand]
    private void ShowSettings()
    {
        CloseDetail();
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
        CloseDetail();
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
    /// <inheritdoc cref="RefreshClusterNavCountsAsync"/>
    private async Task RefreshNavCountsAsync()
    {
        try
        {
            await UpdateNavCountsAsync();
        }
        catch (Exception)
        {
            // Same reasoning as the cluster side: an engine that stopped answering is the containers
            // page's news to break, not a reason for a badge to go blank.
        }
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
