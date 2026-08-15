using Kontena.Sdk.Models;

namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// How a provisioner is allowed to reach the machines it installs on (KON-234).
/// <para>
/// One abstraction with two shapes because the distributions genuinely disagree: kubeadm and k0s want
/// an SSH user and a key, Talos has no shell to SSH into and wants a <c>talosconfig</c> holding client
/// certificates. Which one the wizard asks for follows <see cref="Transport"/> — from the provisioner's
/// own <see cref="ProvisionerCapabilities"/> — and never from a test on the distribution's name.
/// </para>
/// <para>
/// <b>No implementation holds a password.</b> Not because passwords could not be made to work, but
/// because a password the app has to keep in order to reach five machines is precisely the thing not
/// worth keeping. There is no field for one, which is the only way to say it that cannot rot.
/// </para>
/// </summary>
public interface IClusterCredentials
{
    /// <summary>Which transport these are for. The wizard shows the form that matches.</summary>
    ProvisionerTransport Transport { get; }

    /// <summary>
    /// Why these cannot be used, or null when they can. Checked before anything connects, so the
    /// complaint names the field rather than arriving later as somebody else's error.
    /// </summary>
    string? Problem();
}

/// <summary>
/// An SSH user and, optionally, the key to authenticate with — for kubeadm and k0s, which drive the
/// machines over a shell and need sudo once there.
/// <para>
/// The key is a <b>path</b>, never the key. Kontena points at what already exists on this machine and
/// leaves it there, exactly as a remote engine over SSH does (KON-46, KON-261). A null path is not a
/// gap: it means the agent already holds the key, which is the arrangement most people are running.
/// </para>
/// </summary>
/// <param name="User">The user to log in as. Null lets ssh decide, which respects <c>ssh_config</c>.</param>
public sealed record SshCredentials(string? User = null) : IClusterCredentials
{
    public ProvisionerTransport Transport => ProvisionerTransport.Ssh;

    /// <summary>
    /// Path to the private key, or null to let the agent answer. Null is the better default where it
    /// works — but it is not always reachable, and an <c>IdentityAgent</c> pinned elsewhere makes a key
    /// invisible with no way to point at it, which is why the field exists at all.
    /// </summary>
    public string? KeyPath { get; init; }

    /// <summary>
    /// Whether the install steps need <c>sudo</c> on the far side. True by default: kubeadm and k0s
    /// write to <c>/etc</c> and start services, and a user who is already root loses nothing by it.
    /// </summary>
    public bool UseSudo { get; init; } = true;

    public string? Problem() => Problem(null);

    /// <summary>
    /// Why these cannot be used, or null.
    /// </summary>
    /// <param name="agentKeys">
    /// What the SSH agent is offering, or null when it was not asked. An empty list is the answer that
    /// matters: relying on an agent that holds nothing fails at connect time as "Permission denied
    /// (publickey)", which points at the far machine while the problem is here.
    /// </param>
    public string? Problem(IReadOnlyCollection<string>? agentKeys)
    {
        // Same rule as a remote engine's, and deliberately the same code: a leading hyphen in a user or
        // a path is read by ssh as one of its own options, not as a value (KON-181).
        if (RemoteEngine.ArgumentProblem(null, User, null, KeyPath) is { } unsafeValue)
            return unsafeValue;

        if (KeyPath is not { Length: > 0 } key)
        {
            return agentKeys is { Count: 0 }
                ? "No key was given and the SSH agent is holding none, so there is nothing to "
                  + "authenticate with. Add one with ssh-add, or point at a key file."
                : null;
        }

        if (!File.Exists(key))
            return $"No key file at {key}.";

        // The easiest wrong answer, and the one a file picker makes easiest of all — both halves sit
        // side by side and only one of them authenticates.
        return key.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
            ? "That is the public half. SSH authenticates with the private key — the same path without .pub."
            : null;
    }

    /// <summary>
    /// These credentials as they apply to one machine: whatever the host says wins, and the rest falls
    /// back to here.
    /// <para>
    /// One key for the whole cluster is the normal case and one machine differing is the ordinary
    /// exception — a node rebuilt by someone else, a jump box with its own login. Both are the same
    /// mechanism rather than a special case bolted on: the host's fields are already nullable
    /// (KON-233), and null has always meant "as the cluster says".
    /// </para>
    /// </summary>
    public SshCredentials For(RemoteClusterHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return this with
        {
            User = host.User ?? User,
            KeyPath = host.KeyPath ?? KeyPath,
        };
    }
}

/// <summary>
/// A <c>talosconfig</c> — the client certificates <c>talosctl</c> authenticates with. Talos has no SSH
/// and no shell, so this is the whole of it (KON-234).
/// <para>
/// A path again, for the same reason as the SSH key: the file holds a private key, and the way not to
/// leak it is not to copy it. Where there is no file to point at — a config pasted out of
/// <c>talosctl gen config</c> — the contents go to the OS keychain under
/// <c>SecretKeys.Cluster</c> and nowhere else, which is the same treatment an engine password gets.
/// </para>
/// </summary>
public sealed record TalosCredentials : IClusterCredentials
{
    public ProvisionerTransport Transport => ProvisionerTransport.MachineApi;

    /// <summary>Path to the talosconfig, or null when it is held in the keychain instead.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// Whether the config is in the keychain rather than on disk. Not the contents — those never live
    /// on this record, so it cannot be logged or serialised into holding them.
    /// </summary>
    public bool IsStored { get; init; }

    /// <summary>
    /// Which context inside the talosconfig to use, or null for the one it names as current. A
    /// generated config holds exactly one; a shared one can hold several.
    /// </summary>
    public string? Context { get; init; }

    public string? Problem()
    {
        if (IsStored)
            return ConfigPath is { Length: > 0 } ? "Give either a talosconfig file or a stored one, not both." : null;

        if (ConfigPath is not { Length: > 0 } path)
            return "Talos needs a talosconfig — it has no SSH to fall back on. Point at the file talosctl wrote.";

        // Read as an option rather than a path, exactly as ssh would. talosctl takes --talosconfig.
        if (path.StartsWith('-'))
            return "A talosconfig path cannot start with \"-\". It would be read as an option rather than a path.";

        return File.Exists(path) ? null : $"No talosconfig at {path}.";
    }
}
