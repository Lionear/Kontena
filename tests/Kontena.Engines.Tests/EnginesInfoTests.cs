using Kontena.Sdk;
using Xunit;
using Kontena.Engines;

namespace Kontena.Engines.Tests;

public class EnginesInfoTests
{
    [Fact]
    public void Describe_mentions_product_name()
    {
        Assert.Contains("Kontena", EnginesInfo.Describe());
    }
}
