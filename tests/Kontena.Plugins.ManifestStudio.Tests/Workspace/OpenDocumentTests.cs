using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Tests.Workspace;

public sealed class OpenDocumentTests : IDisposable
{
    private readonly string _path = System.IO.Path.GetTempFileName();

    public OpenDocumentTests() => File.WriteAllText(_path, "kind: Deployment\n");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Loading_reads_the_files_current_content_and_starts_clean()
    {
        var document = OpenDocument.Load(_path);

        Assert.Equal("kind: Deployment\n", document.Text);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void Editing_marks_dirty_and_saving_clears_it_and_writes_the_file()
    {
        var document = OpenDocument.Load(_path);

        document.Text = "kind: StatefulSet\n";
        Assert.True(document.IsDirty);

        document.Save();

        Assert.False(document.IsDirty);
        Assert.Equal("kind: StatefulSet\n", File.ReadAllText(_path));
    }

    [Fact]
    public void Editing_back_to_the_saved_text_is_not_dirty()
    {
        var document = OpenDocument.Load(_path);
        var original = document.Text;

        document.Text = "something else";
        document.Text = original;

        Assert.False(document.IsDirty);
    }
}
