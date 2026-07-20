using Kontena.Core;
using Xunit;

namespace Kontena.Core.Tests;

public class ProductInfoTests
{
    [Fact]
    public void Name_is_Kontena()
    {
        Assert.Equal("Kontena", ProductInfo.Name);
    }

    [Fact]
    public void Tagline_is_not_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProductInfo.Tagline));
    }
}
