using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Sdk;

namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// What was in flight when Kontena was last closed (KON-239).
/// <para>
/// A rollout runs <b>from this machine</b>. There is no cluster yet to hand it to — that is exactly
/// what is being built — so closing the app stops k0sctl wherever it had got to. Unlike an upgrade
/// (KON-221), which is handed to the cluster and carries on without us, this one cannot survive us.
/// </para>
/// <para>
/// So it is written down instead: which machines were up, which one stopped, and what the cluster was
/// called. Enough for the next launch to say "four of five were installed" rather than starting from a
/// blank screen and asking someone to remember.
/// </para>
/// </summary>
/// <param name="ClusterName">What was being built.</param>
/// <param name="Standing">Machines that finished, by address. What a resumed run can skip.</param>
/// <param name="Stopped">The machine it stopped on, or null when it was closed mid-flight.</param>
/// <param name="StartedUtc">When it began, so the next launch can say how long ago.</param>
public sealed record RolloutRecord(
    string ClusterName,
    IReadOnlyList<string> Standing,
    string? Stopped,
    DateTimeOffset StartedUtc)
{
    /// <summary>Whether there is anything worth offering to resume.</summary>
    [JsonIgnore]
    public bool IsWorthResuming => Standing.Count > 0 || Stopped is not null;
}

/// <summary>
/// Where an interrupted rollout is remembered between launches.
/// <para>
/// Its own small file rather than a corner of settings: it is not a preference, it is the debris of
/// something that did not finish, and it is deleted the moment the rollout completes. Nothing secret
/// goes in — addresses and a name, no key paths and no key (KON-234).
/// </para>
/// </summary>
public sealed class RolloutRecordStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    /// <param name="path">Where to write. Defaults next to the other application data.</param>
    public RolloutRecordStore(string? path = null) =>
        _path = path ?? Path.Combine(ProductInfo.DataDirectory, "rollout.json");

    /// <summary>The interrupted rollout, or null when there is none — or when it cannot be read.</summary>
    public RolloutRecord? Read()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var record = JsonSerializer.Deserialize<RolloutRecord>(File.ReadAllText(_path), Json);

            // A record naming nothing is not worth offering, and neither is one we cannot parse: this
            // is a convenience, and a broken convenience must not stop the app from starting.
            return record is { IsWorthResuming: true } ? record : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the record. Failing to write loses the resume, never the rollout.</summary>
    public void Write(RolloutRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(record, Json));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing to do about it and nothing worth interrupting a running rollout for.
        }
    }

    /// <summary>Forgets it — on a finished rollout, or once the offer to resume has been declined.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Same as above: a file that will not go is not worth a dialog.
        }
    }
}
