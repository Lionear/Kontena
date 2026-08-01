using Kontena.Plugins.ManifestStudio.Git;

namespace Kontena.Plugins.ManifestStudio.Tests.Git;

public sealed class GitStatusParserTests
{
    [Fact]
    public void A_clean_repository_has_a_branch_and_no_changes()
    {
        var status = GitStatusParser.Parse("## main...origin/main\n");

        Assert.Equal("main", status.Branch);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.Empty(status.Changes);
    }

    [Fact]
    public void Ahead_and_behind_are_read_from_the_branch_header()
    {
        var status = GitStatusParser.Parse("## main...origin/main [ahead 2, behind 1]\n");

        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
    }

    [Fact]
    public void A_branch_with_no_upstream_still_reports_its_name()
    {
        var status = GitStatusParser.Parse("## feature-branch\n");

        Assert.Equal("feature-branch", status.Branch);
        Assert.Equal(0, status.Ahead);
    }

    [Theory]
    [InlineData(" M deployment.yaml", "Modified")]
    [InlineData("A  new-service.yaml", "Added")]
    [InlineData(" D old-config.yaml", "Deleted")]
    [InlineData("?? untracked.yaml", "Untracked")]
    public void Each_status_code_maps_to_a_label(string line, string expected)
    {
        var status = GitStatusParser.Parse($"## main\n{line}\n");

        var change = Assert.Single(status.Changes);
        Assert.Equal(expected, change.Status);
    }

    [Fact]
    public void A_rename_reports_the_new_path()
    {
        var status = GitStatusParser.Parse("## main\nR  old-name.yaml -> new-name.yaml\n");

        var change = Assert.Single(status.Changes);
        Assert.Equal("new-name.yaml", change.Path);
        Assert.Equal("Renamed", change.Status);
    }

    [Fact]
    public void Multiple_changes_are_all_read()
    {
        var status = GitStatusParser.Parse("## main\n M a.yaml\n?? b.yaml\n D c.yaml\n");

        Assert.Equal(["a.yaml", "b.yaml", "c.yaml"], status.Changes.Select(c => c.Path));
        Assert.True(status.HasChanges);
    }
}
