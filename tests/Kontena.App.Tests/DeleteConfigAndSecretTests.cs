using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Deleting a config map or a secret (KON-253). The call is one line; the wording is the feature,
/// because the consequence of this delete is delayed and therefore easy to misjudge.
/// </summary>
public sealed class DeleteConfigAndSecretTests
{
    private static async Task<(ClusterSecretsViewModel Page, ConfirmRequest Request)> SecretDeleteAsync()
    {
        ConfirmRequest? asked = null;
        var page = new ClusterSecretsViewModel(new FakeClusterEngine(), "app")
        {
            RequestConfirm = request => asked = request,
        };
        await page.LoadAsync();

        page.Items.First(r => r.Name == "postgres-credentials").DeleteCommand.Execute(null);

        Assert.NotNull(asked);
        return (page, asked);
    }

    [Fact]
    public async Task Deleting_always_asks_first()
    {
        var (_, request) = await SecretDeleteAsync();

        Assert.True(request.Destructive);
        Assert.Equal("Delete", request.ConfirmLabel);
        Assert.Contains("postgres-credentials", request.Message, StringComparison.Ordinal);
        Assert.Contains("app", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_confirm_says_that_nothing_breaks_now_and_something_breaks_later()
    {
        // This is the part that is easy to get wrong in both directions. "Pods will fail" is untrue —
        // a running pod holds what it mounted at start. "Nothing happens" is worse: the next pod that
        // is recreated will not start, possibly days later, and by then it will not look connected.
        var (_, request) = await SecretDeleteAsync();

        Assert.Contains("nothing breaks now", request.Message, StringComparison.Ordinal);
        Assert.Contains("will not start", request.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_actually_deletes_and_the_list_agrees_afterwards()
    {
        var (page, request) = await SecretDeleteAsync();

        await request.OnConfirm();

        Assert.DoesNotContain(page.Items, r => r.Name == "postgres-credentials");
    }

    [Fact]
    public async Task Not_confirming_deletes_nothing()
    {
        var (page, _) = await SecretDeleteAsync();

        Assert.Contains(page.Items, r => r.Name == "postgres-credentials");
    }

    [Fact]
    public async Task A_config_map_is_named_as_a_config_map_and_not_as_a_secret()
    {
        // Two pages, one act, and the dialog has to be about the thing in front of you.
        ConfirmRequest? asked = null;
        var page = new ClusterConfigMapsViewModel(new FakeClusterEngine(), "app")
        {
            RequestConfirm = request => asked = request,
        };
        await page.LoadAsync();

        page.Items.First(r => r.Name == "web-config").DeleteCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.Equal("Delete config map", asked.Title);
        Assert.Contains("environment", asked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_nobody_wired_a_confirm_to_deletes_nothing()
    {
        // The rule from ViewModelBase, restated where it matters most: no handler, no delete —
        // never a confirm quietly turning into the act it was meant to guard.
        var page = new ClusterSecretsViewModel(new FakeClusterEngine(), "app");
        await page.LoadAsync();

        page.Items.First().DeleteCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(3, page.Items.Count);
    }
}
