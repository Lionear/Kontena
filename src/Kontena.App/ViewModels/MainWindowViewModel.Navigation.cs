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
using Kontena.Sdk.Orchestration;
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
    private void Navigate(string key) => NavigateTo(key);

    /// <param name="refreshNav">
    /// False only where the caller has just read the cluster, the same reason
    /// <see cref="NavigateCluster"/> takes it (KON-375). Opening a cluster reads the picker and the
    /// workload kinds before it builds the first page, because it has to — and then landing on that
    /// page fired a refresh of what had just been read, two cluster-wide lists behind an open shell.
    /// </param>
    private void NavigateTo(string key, bool refreshNav = true)
    {
        // The history step replays as an ordinary visit, refresh and all: coming back to a page later
        // is exactly the moment its sidebar is most likely to be out of date.
        Arrived(NavItems.FirstOrDefault(i => i.Key == key)?.Label ?? key, () => NavigateTo(key));

        // Before the mode switch: a plugin page belongs to neither nav, and both of the switches below
        // fall through to a page of their own on an unknown key.
        if (_pluginPages.TryGetValue(key, out var pluginPage))
        {
            Diag.Mark($"navigate to {key}");
            ShowPluginPage(key, pluginPage);
            return;
        }

        // Marked there rather than here, once (KON-413). Cluster navigation also arrives at
        // NavigateCluster without passing through this method, so a mark in both places logged every
        // sidebar click twice — which read in the diagnostics like a command firing twice, and cost a
        // bug report an afternoon chasing a double click that was never happening.
        if (IsClusterMode)
        {
            NavigateCluster(key, refreshNav);
            return;
        }

        Diag.Mark($"navigate to {key}");

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
            CurrentPage = page.CreateView(this);
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
            // Directly under Overview, and present whether or not this cluster has an Alertmanager
            // (KON-207). "Where are my alerts" is a question that deserves an answer, and hiding the
            // entry makes the page that answers it unreachable — so the capability decides what the
            // page says, not whether it exists.
            new NavItem("alerts", "Alerts", "IconBell"),
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
    /// <param name="refreshNav">
    /// False only where the caller has just read the cluster, so the sidebar is not refetched twice
    /// for one navigation.
    /// </param>
    /// <param name="keepSearch">
    /// The term to put back on the rebuilt page — set only by a reload in place, never by a real
    /// navigation (KON-377).
    /// </param>
    private void NavigateCluster(string key, bool refreshNav = true, string? keepSearch = null)
    {
        if (_cluster is null)
            return;

        Diag.Mark($"navigate to {key}");
        var built = System.Diagnostics.Stopwatch.StartNew();
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
            "overview" => new ClusterOverviewViewModel(_cluster, Versions),
            // RequestConfirm because the metrics-server install writes to the cluster and asks first
            // (KON-93); the other cluster pages route their confirms through the shell callbacks they
            // are handed.
            // The Helm hand-off is the shell's to route, like every other cross-page jump: the page
            // knows a chart should be installed, not where the apply page lives (KON-204).
            // RequestConfirm because the Silenced section's Expire is the page's own delete-shaped
            // write, the same way nodes/workloads confirm their own (KON-208).
            // The refresh interval is read off the store rather than the cached settings, for the
            // reason CurrentTerminalFont gives: a change made in Settings should reach the next page
            // you open, not the next launch. Cluster pages are rebuilt on every visit, so that is
            // also the only moment it needs reading (KON-393).
            "alerts" => new ClusterAlertsViewModel(
                _cluster, onInstallWithHelm: ShowMonitoringHelmInstall, onOpenDetail: ShowAlertDetail,
                onNewRule: () => NavigateCluster("alert-rule"),
                refreshEvery: AlertRefresh.Interval(_store.Load().AlertRefreshSeconds))
                { RequestConfirm = ShowConfirm },
            // Not a nav item: it is an action off the Alerts page, and a permanent sidebar entry
            // called "New rule" would be a page you can be on without having asked for it (KON-210).
            "alert-rule" => new RuleEditorViewModel(_cluster, ApplyAuthoredRule),
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
            "workloads" when WorkloadNavGroups.ShouldGroup(_workloadKinds) =>
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
                // An apply can create or remove anything — refresh the sidebar, not the open page.
                _ = UpdateClusterNavAsync();
                return Task.CompletedTask;
            }, ActiveNamespace),
            _ => new ClusterOverviewViewModel(_cluster, Versions),
        };

        Diag.Mark($"{key} page built in {built.Elapsed.TotalMilliseconds:F1} ms");

        // The sidebar follows the same event the page just reloaded on (KON-339). Set here rather than
        // on each of the constructors above: one place that knows a page is on screen, and the watch
        // having already started in the constructor costs nothing — the callback is read when it
        // fires, not when the stream opens.
        if (CurrentPage is IClusterLivePage live)
            live.Changed = () => _ = RefreshClusterNavAsync();

        // Except the Alerts page, whose refresh is a timer rather than a watch event (KON-393). The
        // rest of the sidebar counts what the apiserver serves, and no interval of ours makes that
        // change — so refreshing it on a clock reads the cluster to be told nothing, however cheap
        // the reads have since become (KON-395, KON-396). What the alerts poll can have changed
        // beside itself is one number: the firing badge, off the same read it just did.
        if (CurrentPage is ClusterAlertsViewModel alerts)
            alerts.Changed = () => _ = UpdateAlertCountAsync();

        // Only the open page's stream drives that callback, so the sidebar stops following the moment
        // you land somewhere that watches nothing — the Workloads dashboard, Config maps, Events. After
        // the page rather than before it, and not awaited: a namespace that appeared is worth a
        // round-trip but never worth making the click wait for one.
        if (refreshNav)
            _ = RefreshClusterNavAsync();

        // The search term does not survive navigating away, and that is the honest behaviour while
        // cluster pages are rebuilt on every visit: the page it filtered no longer exists. The engine
        // pages keep theirs because they are long-lived fields. Restoring a term onto a fresh page
        // would show a filtered list with no way to tell it had been filtered (KON-164).
        //
        // A reload in place is the exception, and the one case where clearing is the dishonest answer
        // (KON-377): the user never left. They clicked Restart or Scale on the one row their search
        // had left standing, and the list they were working in came back showing everything. Put on
        // the page rather than left to the shell's own SearchText, which still holds the term and so
        // raises nothing to push down — a filled box over an unfiltered list.
        if (keepSearch is { Length: > 0 } && CurrentPage is IListPage { SupportsSearch: true } page)
            page.SearchText = keepSearch;

        SearchText = keepSearch ?? string.Empty;
    }
    /// <summary>
    /// Follow the cluster after the open page saw it change (KON-339). Failure is silent on purpose:
    /// this runs off a watch stream nobody asked to be told about, and a picker that could not be
    /// refetched is a list that stays as it was — the page itself reports an unreachable cluster.
    /// </summary>
    private async Task RefreshClusterNavAsync()
    {
        // One in flight at a time (KON-413). Nothing awaits this, and it is fired behind every
        // navigation and every watch event of the open page — so clicking through the sidebar stacked
        // one cluster read per click, each of them answering into a sidebar the next click had already
        // replaced. The last one asked is the only one whose answer is still true.
        var ct = _navRefresh.Begin();

        try
        {
            await Diag.TimeAsync("refresh the sidebar", UpdateClusterNavAsync(ct));
        }
        catch (Exception)
        {
            // Left as they were, which is the same answer a refresh that never ran would give.
        }
    }

    /// <summary>The one sidebar refresh that is out, and the token that ends it (KON-413).</summary>
    private readonly PageLoad _navRefresh = new();

    /// <summary>
    /// Rebuild the currently-selected cluster page (e.g. after an action mutates it), keeping the
    /// search term the user is looking through (KON-377).
    /// </summary>
    private void ReloadCurrentClusterPage()
    {
        if (!IsClusterMode)
            return;

        _ = NavigateClusterAfterKindsAsync(_clusterPageKey, SearchText);
    }

    /// <summary>
    /// Read the workload kinds first, then build the page (KON-200).
    /// <para>
    /// Which page Workloads is — the dashboard or the plain list — depends on how many kinds exist,
    /// and that answer arrives with that read. Navigating first meant deciding on the namespace you
    /// had just left: one kind to several gave the list, several to one gave the dashboard. Both
    /// directions were reported. The same order applies after an apply, which can add the first
    /// DaemonSet or remove the last.
    /// </para>
    /// <para>
    /// That read failing must not cost the navigation — a page built from a stale answer is still
    /// better than no page at all — so the await is guarded and the key resolved either way.
    /// </para>
    /// </summary>
    /// <param name="keepSearch"><inheritdoc cref="NavigateCluster" path="/param[@name='keepSearch']"/></param>
    private async Task NavigateClusterAfterKindsAsync(string key, string? keepSearch = null)
    {
        IsReadingCluster = true;
        try
        {
            await Diag.TimeAsync("read the workload kinds", UpdateClusterNavAsync());
        }
        catch (Exception)
        {
            // Unreachable cluster, a call that timed out: the page itself reports that far better
            // than a nav that never happens.
        }
        finally
        {
            IsReadingCluster = false;
        }

        if (IsClusterMode)
            NavigateCluster(WorkloadNavGroups.ResolveKey(key, _workloadKinds), refreshNav: false, keepSearch);
    }

    /// <summary>
    /// Set while the shell is reading the cluster before it can build a page (KON-375).
    /// <para>
    /// The one wait in cluster mode that nothing on screen could show. Picking a namespace, or an
    /// action that rebuilds the page, goes through <see cref="NavigateClusterAfterKindsAsync"/>, and
    /// that has to know the workload kinds before it can decide which page Workloads even is
    /// (KON-200) — two cluster-wide reads, in front of a page that has not been replaced yet. So the
    /// old page sat there, fully drawn and already wrong, for as long as the cluster took to answer:
    /// the click looked ignored rather than slow, which is the worse of the two.
    /// </para>
    /// <para>
    /// A page's own spinner cannot cover this. The page that would carry it is the one being replaced,
    /// and it is not the one loading.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _isReadingCluster;
    /// <summary>
    /// Bring the sidebar in step with the cluster: the namespace picker and the per-kind Workloads
    /// submenu.
    /// <para>
    /// This used to fill a badge on every entry as well, and that cost twelve cluster-wide list calls
    /// — pods, secrets, configmaps, events and the rest — every time it ran, which is before every
    /// cluster navigation and again on every watch event of the open page. Measured on a 72-pod
    /// cluster (KON-352): 20 MB allocated per round, 250–450 ms, and the UI thread stalled for
    /// 150–330 ms of it. Twelve numbers are not worth that, so they are gone (KON-354).
    /// </para>
    /// <para>
    /// What was left after that was still two cluster-wide reads per navigation, and the workload one
    /// was the most expensive read in the app: five apiserver lists, in full, to decide which of five
    /// submenu entries to draw — two of them Jobs and CronJobs, whose objects never reach the screen
    /// at all. So the submenu asks the question it actually has (<c>ListWorkloadKindsAsync</c>,
    /// KON-396), and the picker follows a watch instead of being re-read behind every click. What is
    /// left here is one cheap read.
    /// </para>
    /// </summary>
    /// <param name="ct">
    /// Ends this read where a later one has already superseded it (KON-413). Default where the caller
    /// awaits it — an awaited refresh cannot be overtaken by itself.
    /// </param>
    private async Task UpdateClusterNavAsync(CancellationToken ct = default)
    {
        if (_cluster is null)
            return;

        var ns = SelectedNamespace == AllNamespaces ? null : SelectedNamespace;

        // Only where the cluster cannot watch, in which case a re-read per navigation is the only way
        // the picker hears about a namespace that was created (KON-343).
        if (_namespaceWatch is null)
            await ReadNamespacesAsync(ct);

        // One call, grouped there, rather than one per kind: five answers arriving separately is five
        // chances for the submenu to disagree with itself and with the list it labels (KON-169).
        var kinds = await _cluster.ListWorkloadKindsAsync(ns, ct);
        ct.ThrowIfCancellationRequested();
        SyncWorkloadKindNav(kinds);

        UpdatePortForwardCount();
        await UpdateAlertCountAsync(ct);
    }

    /// <summary>
    /// The namespaces the picker is built from, read once and then followed (KON-396).
    /// <para>
    /// It used to be re-read in front of every cluster navigation, for a list that changes about as
    /// often as the cluster gains a team. A watch says the same thing for one stream instead of one
    /// round-trip per click, and says it sooner: the picker now hears about a new namespace while you
    /// are standing still, which is the half of KON-343 that polling on navigation never covered.
    /// </para>
    /// </summary>
    private async Task ReadNamespacesAsync(CancellationToken ct = default)
    {
        if (_cluster is null)
            return;

        var namespaces = await _cluster.ListNamespacesAsync(ct);
        ct.ThrowIfCancellationRequested();
        SyncNamespacePicker(namespaces);
    }

    /// <summary>Live while the picker follows the cluster; null when this cluster cannot watch.</summary>
    private CancellationTokenSource? _namespaceWatch;

    /// <summary>
    /// Start following namespaces for the picker, replacing any stream from a cluster before this one.
    /// <see cref="ClusterWatch"/> reloads on the thread that calls this — the UI thread, which is the
    /// only one allowed to touch the collection the picker is bound to.
    /// </summary>
    private void FollowNamespaces()
    {
        StopFollowingNamespaces();

        if (_cluster is null)
            return;

        // A stream that ends puts the reads back rather than saying anything: an apiserver closes a
        // watch on its own schedule, and a picker that stopped following looks exactly like a cluster
        // whose namespaces stopped changing — there is no honest way to draw that on a ComboBox. So
        // dropping the stream is what re-arms the per-navigation read in UpdateClusterNavAsync, and
        // the saving is given up before the freshness is.
        _namespaceWatch = ClusterWatch.Follow(
            _cluster, [GroupVersionKind.Namespace], null, () => ReadNamespacesAsync(),
            (live, _) =>
            {
                if (!live)
                    StopFollowingNamespaces();
            });
    }

    private void StopFollowingNamespaces()
    {
        _namespaceWatch?.Cancel();
        _namespaceWatch?.Dispose();
        _namespaceWatch = null;
    }

    /// <summary>
    /// Badge the sidebar with the number of firing alerts nobody has muted (KON-207).
    /// <para>
    /// Pending is left out because it may never fire, and silenced because somebody already decided
    /// about it — counting either would make the badge mean "alerts" rather than "things to look
    /// at", and a number you learn to ignore is worse than no number.
    /// </para>
    /// <para>
    /// Across every namespace, not just the picked one: Alertmanager does not know about the
    /// namespace picker, and an alert firing outside your current namespace is still your problem.
    /// </para>
    /// </summary>
    /// <param name="ct"><inheritdoc cref="UpdateClusterNavAsync" path="/param[@name='ct']"/></param>
    private async Task UpdateAlertCountAsync(CancellationToken ct = default)
    {
        if (NavItems.FirstOrDefault(i => i.Key == "alerts") is not { } item)
            return;

        if (_cluster is not IAlertingAware { Alerts: var source } || source is NoAlertSource)
        {
            item.Count = string.Empty;
            item.IsLoud = false;
            item.AttentionTip = string.Empty;
            return;
        }

        try
        {
            var firing = ClusterAlertsViewModel.BadgeCount(await source.ListAlertsAsync(ct));

            // No badge at zero rather than a "0": every other entry in this sidebar is silent when
            // it has nothing to say, and an all-clear is not news (KON-219).
            item.Count = firing == 0 ? string.Empty : firing.ToString(CultureInfo.InvariantCulture);
            item.IsLoud = firing > 0;
            item.AttentionTip = firing == 0
                ? string.Empty
                : $"{firing} firing {(firing == 1 ? "alert" : "alerts")}, not silenced";
        }
        // Not on a superseded refresh (KON-413): a read this shell itself cancelled says nothing
        // about Alertmanager, and blanking the badge for it would clear a number that is still right.
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // An Alertmanager that stopped answering is the page's story to tell, not the sidebar's.
            // A stale number here would be the one thing worse than no number.
            item.Count = string.Empty;
            item.IsLoud = false;
            item.AttentionTip = string.Empty;
        }
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

    /// <summary>
    /// Hand off to the apply page's existing Helm source with the monitoring chart filled in
    /// (KON-204 decision 3).
    /// <para>
    /// Kontena ships no copy of kube-prometheus-stack and installs nothing itself. metrics-server
    /// was one pinned manifest with two ClusterRoles; this is a chart with CRDs, retention, storage
    /// and a pile of opinion, and vendoring it means owning its upgrades forever. So the offer is a
    /// route to a flow that already exists, with the fields the user would have typed.
    /// </para>
    /// </summary>
    private void ShowMonitoringHelmInstall()
    {
        NavigateCluster("apply");

        if (CurrentPage is not ApplyManifestViewModel apply)
            return;

        apply.SourceKind = ManifestSourceKind.Helm;
        apply.Chart = "prometheus-community/kube-prometheus-stack";
        apply.ReleaseName = "kube-prometheus-stack";

        // The namespace discovery looks in first, so the install and the search agree by default.
        apply.RenderNamespace = "monitoring";

        // The chart is a repo/name reference, and it resolves to nothing until helm knows the repo:
        // the hand-off used to stop here and leave the user to look the URL up (KON-397). The fields
        // are filled whether or not the add runs, so the panel says which repo this is and its own
        // button can retry it.
        apply.NewRepoName = MonitoringRepoName;
        apply.NewRepoUrl = "https://prometheus-community.github.io/helm-charts";

        // Nothing to add it with, and the page already says helm is missing — a second complaint
        // about the same absence is noise.
        if (apply.IsHelmInstalled)
            _ = AddMonitoringRepoAsync(apply);
    }

    private const string MonitoringRepoName = "prometheus-community";

    /// <summary>
    /// Add the chart's repository behind the hand-off. Failure lands on the page's error line rather
    /// than only in the repository panel's own status: that panel is hidden while helm has no
    /// repositories at all — exactly the case where this add is the one that failed — and a silent
    /// failure would leave the user with a chart that will not resolve and no reason given.
    /// </summary>
    private static async Task AddMonitoringRepoAsync(ApplyManifestViewModel apply)
    {
        if (await apply.UseRepoAsync(MonitoringRepoName, apply.NewRepoUrl))
            return;

        apply.Error = $"Could not add the {MonitoringRepoName} chart repository, so "
            + $"{apply.Chart} will not resolve yet: {apply.RepoStatus}";
    }

    /// <summary>
    /// The rule editor's hand-off (KON-210): the composed manifest goes to the page that already does
    /// server-side dry-run, diff and apply.
    /// <para>
    /// <b>Deliberately not a second apply path.</b> A rule authored in Kontena reaches the cluster the
    /// same way a pasted manifest does, through the same review step — an editor with its own private
    /// Apply button would be the one write in the app that nobody sees the diff of first.
    /// </para>
    /// </summary>
    private void ApplyAuthoredRule(ManifestBundle bundle)
    {
        NavigateCluster("apply");

        if (CurrentPage is not ApplyManifestViewModel apply)
            return;

        apply.SourceKind = ManifestSourceKind.Paste;
        apply.YamlText = bundle.Yaml;
        apply.Source = bundle.Source;
        apply.RenderNamespace = bundle.Namespace;
    }

    /// <summary>Which cluster page is open, including a per-kind workloads page.</summary>
    private string _clusterPageKey = "overview";

    /// <summary>The workload kinds the last read found, which is what decides the Workloads page.</summary>
    private IReadOnlyList<WorkloadKind> _workloadKinds = [];


    /// <summary>
    /// Rebuild the per-kind sub-entries under Workloads (KON-169). Which entries and in what order is
    /// the cluster's answer; this only reconciles the nav collection with it.
    /// </summary>
    private void SyncWorkloadKindNav(IReadOnlyList<WorkloadKind> kinds)
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

        _workloadKinds = kinds;

        // Always listed, never folded (KON-219). The kinds used to hide behind a chevron on the entry
        // above them, which repeated the word the group heading already carries and gave every kind
        // page two routes: this submenu and the dashboard, which does the same job with counts and
        // rollout state. One kind is not a set worth listing — the entry above it already is the page.
        if (!WorkloadNavGroups.ShouldGroup(kinds))
            return;

        var at = parentIndex + 1;
        foreach (var kind in kinds)
        {
            var key = WorkloadNavGroups.KeyFor(kind);

            // No count, like every other entry in this sidebar (KON-354). This one was free — the
            // number comes out of a list that had to be fetched anyway — but "free" is not the reason
            // a number belongs on screen, and five kinds wearing one while nothing around them does
            // reads as the others having lost theirs rather than as a deliberate list.
            items.Insert(at++, new NavItem(key, WorkloadNavGroups.LabelFor(kind), "IconLayers", isChild: true)
            {
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
    /// <inheritdoc cref="RefreshClusterNavAsync"/>
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
