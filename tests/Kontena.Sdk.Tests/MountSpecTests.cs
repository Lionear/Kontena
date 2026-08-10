using Kontena.Sdk.Models;

namespace Kontena.Sdk.Tests;

public sealed class MountSpecTests
{
    /// <summary>
    /// The dictionary this replaces could not carry read-only, and could not hold two mounts sharing
    /// a source — a volume mounted twice at different paths is ordinary, and silently losing one of
    /// them is the kind of gap a green suite hides.
    /// </summary>
    [Fact]
    public void Two_mounts_may_share_one_source()
    {
        var request = new CreateContainerRequest
        {
            Image = "alpine:3.20",
            Mounts =
            [
                new MountSpec(MountSpec.Volume, "data", "/var/lib/data"),
                new MountSpec(MountSpec.Volume, "data", "/backup", ReadOnly: true),
            ],
        };

        Assert.Equal(2, request.Mounts.Count);
        Assert.True(request.Mounts[1].ReadOnly);
        Assert.False(request.Mounts[0].ReadOnly);
    }

    [Fact]
    public void Mounts_default_to_read_write_and_empty()
    {
        var request = new CreateContainerRequest { Image = "alpine:3.20" };

        Assert.Empty(request.Mounts);
        Assert.False(new MountSpec(MountSpec.Bind, "/host", "/in").ReadOnly);
    }
}
