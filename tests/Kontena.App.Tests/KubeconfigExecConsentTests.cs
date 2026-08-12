using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// What the wizard does with a kubeconfig whose contexts log in by running a program (KON-365).
/// <para>
/// Adding a cluster is not an action anyone expects code execution behind, and the file need not be the
/// user's own — kubeconfigs get forwarded, pasted out of a ticket, pulled from a repo. So the command is
/// shown before the first connection, and the first connection is the reachability probe the wizard
/// starts the moment the file is read, not the "Test connection" button.
/// </para>
/// </summary>
public sealed class KubeconfigExecConsentTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), $"kontena-exec-{Guid.NewGuid():N}.json");

    private readonly string _kubeconfigPath = Path.Combine(
        Directory.CreateTempSubdirectory("kontena-exec-kubeconfig").FullName, "config");

    public void Dispose()
    {
        if (File.Exists(_settingsPath))
            File.Delete(_settingsPath);
    }

    /// <summary>
    /// One context behind an exec plugin and one with a certificate. The plain one points at a closed
    /// loopback port on purpose: the wizard probes it for real, and a refused connection answers at once.
    /// </summary>
    private const string ConfigYaml = """
        apiVersion: v1
        kind: Config
        current-context: gke-prod
        clusters:
          - name: gke-prod
            cluster:
              server: https://34.90.10.11
          - name: kind-dev
            cluster:
              server: https://127.0.0.1:1
        contexts:
          - name: gke-prod
            context:
              cluster: gke-prod
              user: gke-prod
          - name: kind-dev
            context:
              cluster: kind-dev
              user: kind-dev
        users:
          - name: gke-prod
            user:
              exec:
                apiVersion: client.authentication.k8s.io/v1beta1
                command: gke-gcloud-auth-plugin
                args:
                  - --account
                  - someone@example.com
          - name: kind-dev
            user:
              client-certificate-data: Zm9v
              client-key-data: YmFy
        """;

    private const string GkeCommand = "gke-gcloud-auth-plugin --account someone@example.com";

    private SettingsStore Store()
    {
        File.WriteAllText(_kubeconfigPath, ConfigYaml);
        return new SettingsStore(_settingsPath);
    }

    /// <summary>
    /// The wizard on step 2, pointed at the test kubeconfig — setting the path is what reads it. The step
    /// is set afterwards rather than through <c>ChooseKubernetes</c>, which would read whatever kubeconfig
    /// the machine running the tests happens to have.
    /// </summary>
    private static AddBackendViewModel Wizard(SettingsStore store, string kubeconfigPath)
    {
        var wizard = new AddBackendViewModel(
            store, [], onClose: () => { }, onAdded: _ => Task.CompletedTask);

        wizard.KubeconfigPath = kubeconfigPath;
        wizard.Step = AddBackendStep.Kubernetes;
        return wizard;
    }

    private AddBackendViewModel Wizard() => Wizard(Store(), _kubeconfigPath);

    private static KubeContextChoice Context(AddBackendViewModel wizard, string name) =>
        wizard.Contexts.Single(c => c.Name == name);

    [Fact]
    public void The_command_a_context_would_run_is_shown_before_anything_is_contacted()
    {
        var wizard = Wizard();

        var pending = Assert.Single(wizard.PendingExec);
        Assert.Equal("gke-prod", pending.Name);
        Assert.Equal(GkeCommand, pending.ExecCommand);
        Assert.True(wizard.HasPendingExec);
    }

    [Fact]
    public void A_context_waiting_on_that_answer_is_not_probed()
    {
        var wizard = Wizard();

        // "checking…" would be a lie: the probe is the connection, and the connection is what runs the
        // command. Nothing has been started for this context at all.
        var gke = Context(wizard, "gke-prod");
        Assert.True(gke.NeedsExecConsent);
        Assert.False(gke.IsProbing);
        Assert.Equal("needs your answer", gke.StatusLabel);
    }

    [Fact]
    public void A_context_with_ordinary_credentials_is_not_held_up_by_it()
    {
        var wizard = Wizard();

        Assert.False(Context(wizard, "kind-dev").NeedsExecConsent);
        Assert.DoesNotContain(wizard.PendingExec, c => c.Name == "kind-dev");
    }

    [Fact]
    public void The_test_button_is_off_while_a_selected_context_is_unanswered()
    {
        var wizard = Wizard();

        Assert.True(Context(wizard, "gke-prod").IsSelected);
        Assert.False(wizard.CanContinue);
    }

    [Fact]
    public void Unticking_it_lets_the_rest_of_the_file_go_ahead()
    {
        // Not a wall: the warning is about one context, and the others in the same file are still addable.
        var wizard = Wizard();

        Context(wizard, "gke-prod").IsSelected = false;

        Assert.True(Context(wizard, "kind-dev").IsSelected);
        Assert.True(wizard.CanContinue);
    }

    [Fact]
    public void Answering_it_records_the_command_and_lets_the_context_through()
    {
        var store = Store();
        var wizard = Wizard(store, _kubeconfigPath);

        wizard.AllowExecCommandsCommand.Execute(null);

        Assert.False(Context(wizard, "gke-prod").NeedsExecConsent);
        Assert.False(wizard.HasPendingExec);
        Assert.True(wizard.CanContinue);
        Assert.True(store.Load().AllowsExecCredential("gke-prod", GkeCommand));
    }

    [Fact]
    public void A_command_that_was_answered_before_is_not_asked_about_again()
    {
        var store = Store();
        store.Update(s => s.WithAllowedExecCredential("gke-prod", GkeCommand));

        var wizard = Wizard(store, _kubeconfigPath);

        Assert.Empty(wizard.PendingExec);
        Assert.False(Context(wizard, "gke-prod").NeedsExecConsent);
    }

    [Fact]
    public void The_same_context_naming_a_different_command_is_asked_about_again()
    {
        // The answer was about running that program, not about trusting the context's name — a kubeconfig
        // that is edited underneath it is a new question, the way a changed plugin is.
        var store = Store();
        store.Update(s => s.WithAllowedExecCredential("gke-prod", "gke-gcloud-auth-plugin"));

        var wizard = Wizard(store, _kubeconfigPath);

        Assert.Equal(GkeCommand, Assert.Single(wizard.PendingExec).ExecCommand);
    }
}
