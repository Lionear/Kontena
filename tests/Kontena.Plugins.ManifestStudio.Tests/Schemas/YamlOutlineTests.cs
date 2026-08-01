using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Tests.Schemas;

public sealed class YamlOutlineTests
{
    [Fact]
    public void A_bare_scalar_list_item_keeps_its_value()
    {
        var outline = YamlOutline.Parse("labels:\n  - app.kubernetes.io/name\n  - app.kubernetes.io/part-of\n");

        var labels = outline.Children.Single(c => c.Key == "labels");

        Assert.Equal(
            ["app.kubernetes.io/name", "app.kubernetes.io/part-of"],
            labels.Children.Select(c => c.InlineValue));
        Assert.All(labels.Children, c => Assert.True(c.IsArrayItem));
    }

    [Fact]
    public void A_key_value_list_item_is_unaffected_by_the_bare_scalar_case()
    {
        var outline = YamlOutline.Parse("resources:\n  - path: deployment.yaml\n");

        var item = outline.Children.Single(c => c.Key == "resources").Children.Single();

        Assert.Null(item.InlineValue);
        Assert.Equal("deployment.yaml", item.Children.Single(c => c.Key == "path").InlineValue);
    }
}
