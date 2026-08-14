using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// What a document says it is: the <c>apiVersion</c>/<c>kind</c> pair at its top level, as the triple
/// <see cref="SchemaIndex"/> resolves against (KON-296).
/// <para>
/// Null while the document does not declare both. That is the ordinary state of a file being typed,
/// not an error — see Plan §4: the path resolution has to work on YAML that is still broken.
/// </para>
/// </summary>
public static class ManifestKind
{
    public static GroupVersionKind? Of(string document)
    {
        var outline = YamlOutline.Parse(document);
        var apiVersion = outline.Children.FirstOrDefault(c => c.Key == "apiVersion")?.InlineValue;
        var kind = outline.Children.FirstOrDefault(c => c.Key == "kind")?.InlineValue;
        if (apiVersion is null || kind is null)
            return null;

        var (group, version) = ManifestDiagnostics.SplitApiVersion(apiVersion);
        return new GroupVersionKind(group, version, kind);
    }
}
