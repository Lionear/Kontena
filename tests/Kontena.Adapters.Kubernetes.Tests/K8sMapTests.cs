using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Kontena.Core.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The mapper is where this adapter can quietly get things wrong — a misread quantity or an owner
/// reference resolved to the wrong controller looks plausible in the UI. These run against
/// hand-built API objects, so they need no cluster and are safe in CI.
/// </summary>
public class K8sMapTests
{
    private static V1Node Node(
        string name = "worker-1",
        string readyStatus = "True",
        bool diskPressure = false,
        IDictionary<string, string>? labels = null,
        bool unschedulable = false) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            Labels = labels,
            CreationTimestamp = DateTime.UtcNow.AddDays(-3),
        },
        Spec = new V1NodeSpec { Unschedulable = unschedulable },
        Status = new V1NodeStatus
        {
            NodeInfo = new V1NodeSystemInfo
            {
                KubeletVersion = "v1.35.0",
                OsImage = "Debian GNU/Linux 12",
                Architecture = "amd64", BootID = "b", ContainerRuntimeVersion = "containerd://2.0",
                KernelVersion = "6.1", KubeProxyVersion = "v1.35.0", MachineID = "m", OperatingSystem = "linux",
                SystemUUID = "u",
            },
            Addresses = [new V1NodeAddress { Type = "InternalIP", Address = "10.0.0.7" }],
            Allocatable = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new("4"),
                ["memory"] = new("8Gi"),
                ["pods"] = new("110"),
            },
            Conditions =
            [
                new V1NodeCondition { Type = "Ready", Status = readyStatus, Reason = "KubeletReady", Message = "kubelet is posting ready status" },
                new V1NodeCondition { Type = "DiskPressure", Status = diskPressure ? "True" : "False", Reason = "KubeletHasNoDiskPressure" },
            ],
        },
    };

    [Fact]
    public void Node_maps_identity_capacity_and_age()
    {
        var node = K8sMap.ToNode(Node(), usage: null);

        Assert.Equal("worker-1", node.Name);
        Assert.Equal("Ready", node.Status);
        Assert.Equal("v1.35.0", node.KubeletVersion);
        Assert.Equal("10.0.0.7", node.InternalIp);
        Assert.Equal(4000, node.Capacity.CpuMillicores);
        Assert.Equal(8L * 1024 * 1024 * 1024, node.Capacity.MemoryBytes);
        Assert.Equal(110, node.Capacity.Pods);
        Assert.Equal(3, node.Age.Days);
        Assert.Null(node.Usage);
    }

    [Theory]
    [InlineData("True", "Ready")]
    [InlineData("False", "NotReady")]
    [InlineData("Unknown", "Unknown")]
    public void Node_status_summarises_the_ready_condition(string condition, string expected)
    {
        Assert.Equal(expected, K8sMap.ToNode(Node(readyStatus: condition), usage: null).Status);
    }

    [Fact]
    public void Node_roles_come_from_labels_and_default_to_worker()
    {
        var labelled = K8sMap.ToNode(
            Node(labels: new Dictionary<string, string> { ["node-role.kubernetes.io/control-plane"] = "" }), null);
        Assert.Equal(["control-plane"], labelled.Roles);

        Assert.Equal(["worker"], K8sMap.ToNode(Node(), null).Roles);
    }

    [Fact]
    public void Node_conditions_are_reported_and_only_the_bad_ones_count_as_problems()
    {
        var healthy = K8sMap.ToNode(Node(), null);
        Assert.Equal(2, healthy.Conditions.Count);
        Assert.Empty(healthy.Problems);

        // Ready reads the opposite way round to the pressure conditions.
        var notReady = K8sMap.ToNode(Node(readyStatus: "False"), null);
        Assert.Equal("Ready", Assert.Single(notReady.Problems).Type);

        var pressured = K8sMap.ToNode(Node(diskPressure: true), null);
        Assert.Equal("DiskPressure", Assert.Single(pressured.Problems).Type);
    }

    [Fact]
    public void Node_conditions_do_not_need_a_metrics_source()
    {
        // The whole point of separating conditions from usage: no metrics, still full health.
        var node = K8sMap.ToNode(Node(readyStatus: "False"), usage: null);

        Assert.Null(node.Usage);
        Assert.NotEmpty(node.Conditions);
        Assert.NotEmpty(node.Problems);
    }

    [Fact]
    public void Scheduled_pod_count_survives_without_a_metrics_source()
    {
        // It is counted from the pod list, so a cluster with no metrics-server still shows "12 / 110".
        var node = K8sMap.ToNode(Node(), usage: null, scheduledPods: 12);

        Assert.Null(node.Usage);
        Assert.Equal(12, node.ScheduledPods);
        Assert.Equal(110, node.Capacity.Pods);
    }

    // ── Pods ─────────────────────────────────────────────────────────────────

    private static V1Pod Pod(V1OwnerReference? owner = null, string phase = "Running") => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "web-5f2a8c-xyz",
            NamespaceProperty = "app",
            OwnerReferences = owner is null ? null : [owner],
            CreationTimestamp = DateTime.UtcNow.AddHours(-2),
        },
        Spec = new V1PodSpec { NodeName = "worker-1", Containers = [] },
        Status = new V1PodStatus
        {
            Phase = phase,
            PodIP = "10.4.1.9",
            QosClass = "Burstable",
            ContainerStatuses =
            [
                new V1ContainerStatus
                {
                    Name = "web", Image = "nginx:1.27", Ready = true, RestartCount = 2,
                    State = new V1ContainerState { Running = new V1ContainerStateRunning() },
                },
                new V1ContainerStatus
                {
                    Name = "sidecar", Image = "envoy:1.30", Ready = false, RestartCount = 5,
                    State = new V1ContainerState
                    {
                        Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff" },
                    },
                },
            ],
        },
    };

    [Fact]
    public void Pod_maps_status_containers_and_restart_total()
    {
        var pod = K8sMap.ToPod(Pod());

        Assert.Equal("web-5f2a8c-xyz", pod.Name);
        Assert.Equal("app", pod.Namespace);
        Assert.Equal(PodPhase.Running, pod.Phase);
        Assert.Equal(QosClass.Burstable, pod.Qos);
        Assert.Equal("10.4.1.9", pod.Ip);
        Assert.Equal(7, pod.Restarts);          // summed across containers
        Assert.Equal(1, pod.ReadyContainers);
        Assert.Equal("Running", pod.Containers[0].State);
        Assert.Equal("Waiting: CrashLoopBackOff", pod.Containers[1].State);
    }

    /// <summary>
    /// A pod part-way through its init containers: the first finished, the second is looping, and the
    /// app container is waiting on both. This is the shape the whole of KON-168 is about.
    /// </summary>
    private static V1Pod InitialisingPod() => new()
    {
        Metadata = new V1ObjectMeta { Name = "migrate-9b4f", NamespaceProperty = "app", CreationTimestamp = DateTime.UtcNow.AddMinutes(-6) },
        Spec = new V1PodSpec
        {
            NodeName = "worker-1",
            InitContainers =
            [
                new V1Container { Name = "wait-for-db", Image = "busybox:1.36" },
                new V1Container { Name = "run-migrations", Image = "migrate:2.1" },
            ],
            Containers =
            [
                new V1Container
                {
                    Name = "app", Image = "api:1.8",
                    Ports =
                    [
                        new V1ContainerPort { ContainerPort = 8080, Name = "http", Protocol = "TCP" },
                        new V1ContainerPort { ContainerPort = 9090, Name = "metrics", Protocol = "TCP" },
                    ],
                },
            ],
        },
        Status = new V1PodStatus
        {
            Phase = "Pending",
            InitContainerStatuses =
            [
                new V1ContainerStatus
                {
                    Name = "wait-for-db", Image = "busybox:1.36", Ready = true, RestartCount = 0,
                    State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { Reason = "Completed", ExitCode = 0 } },
                },
                new V1ContainerStatus
                {
                    Name = "run-migrations", Image = "migrate:2.1", Ready = false, RestartCount = 4,
                    State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff" } },
                },
            ],
            ContainerStatuses =
            [
                new V1ContainerStatus
                {
                    Name = "app", Image = "api:1.8", Ready = false, RestartCount = 0,
                    State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "PodInitializing" } },
                },
            ],
        },
    };

    [Fact]
    public void Init_containers_are_mapped_separately_from_app_containers()
    {
        var pod = K8sMap.ToPod(InitialisingPod());

        Assert.Equal(["wait-for-db", "run-migrations"], pod.InitContainers.Select(c => c.Name));
        Assert.Equal(["app"], pod.Containers.Select(c => c.Name));
        Assert.All(pod.InitContainers, c => Assert.Equal(ContainerKind.Init, c.Kind));

        // Containers keeps meaning "app containers" so the x/y column still counts what kubectl counts.
        Assert.Single(pod.Containers);
    }

    [Fact]
    public void All_containers_lists_init_first_because_that_is_the_order_they_run()
    {
        var pod = K8sMap.ToPod(InitialisingPod());

        Assert.Equal(["wait-for-db", "run-migrations", "app"], pod.AllContainers.Select(c => c.Name));
    }

    [Fact]
    public void Init_restarts_count_towards_the_pod_total()
    {
        // Four attempts at an init container is four restarts. Reporting 0 is the reading that hides
        // exactly the pod you went looking for.
        Assert.Equal(4, K8sMap.ToPod(InitialisingPod()).Restarts);
    }

    [Fact]
    public void A_pod_stuck_in_init_reports_the_reason_rather_than_Pending()
    {
        var pod = K8sMap.ToPod(InitialisingPod());

        Assert.True(pod.IsInitialising);
        Assert.Equal(1, pod.CompletedInitContainers);
        Assert.Equal("Init:CrashLoopBackOff", pod.StatusText);
    }

    [Fact]
    public void A_pod_merely_starting_up_counts_its_init_containers()
    {
        // PodInitializing is the kubelet saying "not yet", which the x/y count says better. Only a
        // reason that names a problem should replace it.
        var source = InitialisingPod();
        source.Status.InitContainerStatuses[1].State =
            new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "PodInitializing" } };

        Assert.Equal("Init:1/2", K8sMap.ToPod(source).StatusText);
    }

    [Fact]
    public void A_pod_past_its_init_containers_reports_its_phase()
    {
        var source = InitialisingPod();
        source.Status.Phase = "Running";
        source.Status.InitContainerStatuses[1].State =
            new V1ContainerState { Terminated = new V1ContainerStateTerminated { Reason = "Completed", ExitCode = 0 } };

        var pod = K8sMap.ToPod(source);

        Assert.False(pod.IsInitialising);
        Assert.Equal("Running", pod.StatusText);
    }

    [Fact]
    public void Declared_container_ports_reach_the_model()
    {
        // The port-forward dialog had nothing to offer for a pod because these were never read (KON-170).
        var app = K8sMap.ToPod(InitialisingPod()).Containers[0];

        Assert.Equal([8080, 9090], app.Ports.Select(p => p.Number));
        Assert.Equal("http", app.Ports[0].Name);
        Assert.Equal("TCP", app.Ports[0].Protocol);
    }

    [Fact]
    public void A_container_that_declares_no_ports_gets_an_empty_list()
    {
        // Empty means "declared nothing", never "listening on nothing" — containerPort is documentation
        // in Kubernetes, so inventing a default here would be inventing a fact.
        Assert.Empty(K8sMap.ToPod(InitialisingPod()).InitContainers[0].Ports);
    }

    [Fact]
    public void A_finished_init_container_cannot_be_exec_into()
    {
        var pod = K8sMap.ToPod(InitialisingPod());

        Assert.True(pod.InitContainers[0].CompletedSuccessfully);
        Assert.False(pod.InitContainers[0].CanExec);
    }

    [Fact]
    public void Pod_owned_by_a_ReplicaSet_rolls_up_to_its_Deployment()
    {
        // Users think in Deployments, not the ReplicaSet the controller actually created.
        var pod = K8sMap.ToPod(Pod(new V1OwnerReference
        {
            ApiVersion = "apps/v1", Kind = "ReplicaSet", Name = "web-5f2a8c", Uid = "1", Controller = true,
        }));

        Assert.Equal("Deployment/web", pod.ControlledBy);
    }

    [Fact]
    public void Pod_owned_by_another_kind_keeps_that_kind()
    {
        var pod = K8sMap.ToPod(Pod(new V1OwnerReference
        {
            ApiVersion = "apps/v1", Kind = "DaemonSet", Name = "cilium", Uid = "1", Controller = true,
        }));

        Assert.Equal("DaemonSet/cilium", pod.ControlledBy);
    }

    [Fact]
    public void Pod_without_an_owner_reports_none()
    {
        Assert.Empty(K8sMap.ToPod(Pod()).ControlledBy);
    }

    // ── Workloads ────────────────────────────────────────────────────────────

    private static V1Deployment Deployment(int desired, int ready, int updated) => new()
    {
        Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "app", CreationTimestamp = DateTime.UtcNow },
        Spec = new V1DeploymentSpec
        {
            Replicas = desired,
            Selector = new V1LabelSelector(),
            Template = new V1PodTemplateSpec
            {
                Spec = new V1PodSpec { Containers = [new V1Container { Name = "api", Image = "ghcr.io/api:1.8" }] },
            },
        },
        Status = new V1DeploymentStatus { ReadyReplicas = ready, UpdatedReplicas = updated, AvailableReplicas = ready },
    };

    [Theory]
    [InlineData(3, 3, 3, RolloutStatus.Complete)]
    [InlineData(3, 2, 2, RolloutStatus.Progressing)]
    [InlineData(3, 3, 1, RolloutStatus.Progressing)]
    [InlineData(3, 0, 0, RolloutStatus.Degraded)]
    [InlineData(0, 0, 0, RolloutStatus.Paused)]
    public void Deployment_rollout_status_follows_the_replica_counts(
        int desired, int ready, int updated, RolloutStatus expected)
    {
        Assert.Equal(expected, K8sMap.ToWorkload(Deployment(desired, ready, updated)).RolloutStatus);
    }

    [Fact]
    public void Deployment_maps_kind_counts_and_images()
    {
        var workload = K8sMap.ToWorkload(Deployment(3, 3, 3));

        Assert.Equal(WorkloadKind.Deployment, workload.Kind);
        Assert.Equal("api", workload.Name);
        Assert.Equal("app", workload.Namespace);
        Assert.Equal(3, workload.Desired);
        Assert.Equal(["ghcr.io/api:1.8"], workload.Images);
        Assert.True(workload.IsScalable);
    }

    [Fact]
    public void DaemonSet_counts_come_from_scheduling_not_replicas()
    {
        var ds = new V1DaemonSet
        {
            Metadata = new V1ObjectMeta { Name = "node-exporter", NamespaceProperty = "monitoring" },
            Spec = new V1DaemonSetSpec { Selector = new V1LabelSelector(), Template = new V1PodTemplateSpec() },
            Status = new V1DaemonSetStatus
            {
                DesiredNumberScheduled = 4, NumberReady = 4, UpdatedNumberScheduled = 4, NumberAvailable = 4,
                CurrentNumberScheduled = 4, NumberMisscheduled = 0,
            },
        };

        var workload = K8sMap.ToWorkload(ds);

        Assert.Equal(WorkloadKind.DaemonSet, workload.Kind);
        Assert.Equal(4, workload.Desired);
        Assert.Equal(4, workload.Ready);
        Assert.False(workload.IsScalable);
        Assert.Equal(RolloutStatus.Complete, workload.RolloutStatus);
    }

    [Fact]
    public void CronJob_carries_its_schedule_and_is_healthy_unless_suspended()
    {
        V1CronJob Cron(bool suspend) => new()
        {
            Metadata = new V1ObjectMeta { Name = "backup", NamespaceProperty = "app" },
            Spec = new V1CronJobSpec
            {
                Schedule = "0 3 * * *",
                Suspend = suspend,
                JobTemplate = new V1JobTemplateSpec { Spec = new V1JobSpec { Template = new V1PodTemplateSpec() } },
            },
        };

        var active = K8sMap.ToWorkload(Cron(suspend: false));
        Assert.Equal(WorkloadKind.CronJob, active.Kind);
        Assert.Equal("0 3 * * *", active.Schedule);

        // A CronJob has no replicas to be unhealthy about — only suspension changes its status.
        Assert.Equal(RolloutStatus.Complete, active.RolloutStatus);
        Assert.Equal(RolloutStatus.Paused, K8sMap.ToWorkload(Cron(suspend: true)).RolloutStatus);
    }

    // ── Services ─────────────────────────────────────────────────────────────

    private static V1Service Service(string type, string clusterIp) => new()
    {
        Metadata = new V1ObjectMeta { Name = "web", NamespaceProperty = "app" },
        Spec = new V1ServiceSpec
        {
            Type = type,
            ClusterIP = clusterIp,
            Selector = new Dictionary<string, string> { ["app"] = "web" },
            Ports =
            [
                new V1ServicePort
                {
                    Name = "http", Port = 80, TargetPort = "8080", Protocol = "TCP", NodePort = 31080,
                },
            ],
        },
    };

    [Fact]
    public void Service_without_a_cluster_ip_is_headless()
    {
        // Kubernetes calls it ClusterIP with clusterIP: None; Kontena models it as its own kind.
        Assert.Equal(ServiceType.Headless, K8sMap.ToService(Service("ClusterIP", "None")).Type);
        Assert.Equal(ServiceType.ClusterIp, K8sMap.ToService(Service("ClusterIP", "10.0.0.5")).Type);
        Assert.Equal(ServiceType.LoadBalancer, K8sMap.ToService(Service("LoadBalancer", "10.0.0.5")).Type);
        Assert.Equal(ServiceType.NodePort, K8sMap.ToService(Service("NodePort", "10.0.0.5")).Type);
    }

    [Fact]
    public void Service_ports_carry_target_and_node_port()
    {
        var port = Assert.Single(K8sMap.ToService(Service("NodePort", "10.0.0.5")).Ports);

        Assert.Equal("http", port.Name);
        Assert.Equal(80, port.Port);
        Assert.Equal(8080, port.TargetPort);
        Assert.Equal(31080, port.NodePort);
        Assert.Equal("TCP", port.Protocol);
    }

    // ── Quantities ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("500m", 500)]
    [InlineData("2", 2000)]
    [InlineData("1500m", 1500)]
    public void Cpu_quantities_become_millicores(string quantity, long expected)
    {
        var node = Node();
        node.Status.Allocatable["cpu"] = new ResourceQuantity(quantity);

        Assert.Equal(expected, K8sMap.ToNode(node, null).Capacity.CpuMillicores);
    }

    [Theory]
    [InlineData("1Gi", 1073741824L)]
    [InlineData("512Mi", 536870912L)]
    [InlineData("1000000", 1000000L)]
    public void Memory_quantities_become_bytes(string quantity, long expected)
    {
        var node = Node();
        node.Status.Allocatable["memory"] = new ResourceQuantity(quantity);

        Assert.Equal(expected, K8sMap.ToNode(node, null).Capacity.MemoryBytes);
    }

    [Fact]
    public void A_missing_quantity_reads_as_zero_rather_than_throwing()
    {
        var node = Node();
        node.Status.Allocatable.Clear();

        var mapped = K8sMap.ToNode(node, null);

        Assert.Equal(0, mapped.Capacity.CpuMillicores);
        Assert.Equal(0, mapped.Capacity.MemoryBytes);
    }

    // ── Events ───────────────────────────────────────────────────────────────

    [Fact]
    public void Event_maps_severity_and_the_object_it_is_about()
    {
        var mapped = K8sMap.ToEvent(new Corev1Event
        {
            Metadata = new V1ObjectMeta { Name = "redis-0.17c" },
            InvolvedObject = new V1ObjectReference
            {
                Kind = "Pod", Name = "redis-0", NamespaceProperty = "app", ApiVersion = "v1",
            },
            Reason = "BackOff",
            Message = "Back-off restarting failed container",
            Type = "Warning",
            Count = 7,
            Source = new V1EventSource { Component = "kubelet" },
            LastTimestamp = DateTime.UtcNow,
        });

        Assert.Equal(EventSeverity.Warning, mapped.Severity);
        Assert.Equal("BackOff", mapped.Reason);
        Assert.Equal("kubelet", mapped.Source);
        Assert.Equal(7, mapped.Count);
        Assert.Equal("Pod", mapped.InvolvedObject.Kind.Kind);
        Assert.Equal("app", mapped.InvolvedObject.Namespace);
        Assert.Equal("redis-0", mapped.InvolvedObject.Name);
    }
}
