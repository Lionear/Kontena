using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The ports cell trims, and a trimmed cell used to be the end of the road: the ellipsis ran into the
/// next column and there was no way to read what was cut off (KON-199). The cell keeps its one-line
/// join; the tooltip carries the same ports one per line.
/// </summary>
public sealed class PortsColumnTests
{
    private static PodContainerRow Row(params ContainerPort[] ports) =>
        new(new ContainerStatus { Name = "web", Image = "nginx:1.27", Ready = true, Ports = ports });

    private static ServiceRow Service(params ServicePort[] ports) =>
        new(new Service { Name = "web", Namespace = "app", Ports = ports });

    [Fact]
    public void A_container_tooltip_lists_every_port_on_its_own_line()
    {
        var row = Row(
            new ContainerPort("metrics", 9100, "TCP"),
            new ContainerPort("traefik", 9000, "TCP"),
            new ContainerPort("web", 8000, "TCP"),
            new ContainerPort(string.Empty, 8443, "TCP"));

        Assert.Equal(
            "9100/TCP metrics\n9000/TCP traefik\n8000/TCP web\n8443/TCP",
            row.PortsTooltip);
    }

    [Fact]
    public void The_cell_and_the_tooltip_show_the_same_ports()
    {
        // Two renderings of one list — a tooltip that disagrees with the cell it explains is worse
        // than none.
        var row = Row(new ContainerPort("web", 8000, "TCP"), new ContainerPort("metrics", 9100, "TCP"));

        Assert.Equal(row.PortsText.Split(", "), row.PortsTooltip!.Split('\n'));
    }

    [Fact]
    public void No_ports_means_no_tooltip()
    {
        // The cell reads "—"; a tooltip there would open to say nothing.
        Assert.Equal("—", Row().PortsText);
        Assert.Null(Row().PortsTooltip);

        Assert.Equal("—", Service().Ports);
        Assert.Null(Service().PortsTooltip);
    }

    [Fact]
    public void A_service_tooltip_keeps_the_node_port_form()
    {
        var service = Service(
            new ServicePort("http", 80, 8080, 30080, "TCP"),
            new ServicePort("https", 443, 8443, null, "TCP"));

        Assert.Equal("80:30080/TCP\n443/TCP", service.PortsTooltip);
        Assert.Equal("80:30080/TCP  443/TCP", service.Ports);
    }
}
