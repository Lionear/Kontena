using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// The page says what it is doing while it does it (KON-381). A bundle is a plan that only appears
/// once every document has an outcome, and kube-prometheus-stack spends a second parsing, a hundred
/// round trips applying and up to thirty seconds waiting for its CRDs before the first row lands —
/// which read as a hung window.
/// </summary>
public class ApplyProgressTests
{
    private const string TwoDocuments = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: first
          namespace: app
        ---
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: second
          namespace: app
        """;

    [Fact]
    public async Task A_run_counts_its_way_through_the_bundle_and_stops_talking_when_it_is_done()
    {
        // Progress<T> reports through the context it was made on; run them here so they are observable.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            var vm = new ApplyManifestViewModel(new FakeClusterEngine(), "kind-test")
            {
                YamlText = TwoDocuments,
            };

            var said = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.Status) && vm.Status.Length > 0)
                    said.Add(vm.Status);
            };

            await vm.DryRunCommand.ExecuteAsync(null);

            Assert.Contains("Checking 1 of 2", said);
            Assert.Contains("Checking 2 of 2", said);

            // Nothing is happening any more, so the page must not still claim something is.
            Assert.Equal(string.Empty, vm.Status);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
