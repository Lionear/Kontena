using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kontena.App.Services;

/// <summary>
/// Linux: the Secret Service, through libsecret — the same store GNOME Keyring and KWallet expose, so
/// entries show up in Seahorse or KWallet and can be revoked there (KON-52).
/// <para>
/// libsecret's password API is variadic (attribute name/value pairs terminated by NULL). Rather than
/// marshal that, this binds the <c>_sync</c> functions with exactly one attribute pair — which is all a
/// single-key lookup needs — and declares the schema to match. The secret never goes through a command
/// line: <c>secret-tool store</c> would put it in argv, where any other process can read it from
/// <c>ps</c>.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
[SuppressMessage("Globalization", "CA2101",
    Justification = "The strings are marshalled as LPUTF8Str, which is what glib takes. The rule wants CharSet.Unicode or LPWStr; using UTF-16 here would be actively wrong, and the ANSI conversion the rule guards against is not in play.")]
public sealed class LibSecretStore : ISecretStore
{
    private const string Library = "libsecret-1.so.0";

    /// <summary>The attribute every entry carries, and the only one looked up by.</summary>
    private const string KeyAttribute = "kontena-key";

    private readonly Lazy<IntPtr> _schema = new(CreateSchema);

    /// <summary>
    /// Whether libsecret loads *and* a Secret Service answers. Both are needed and neither is
    /// guaranteed: the library can be absent, and on a headless session nothing implements the service.
    /// Answered by attempting a lookup, because that is the only honest test — a library that loads
    /// tells you nothing about a daemon that is not running.
    /// </summary>
    public bool IsAvailable => _available ??= Probe();

    private bool? _available;

    private bool Probe()
    {
        try
        {
            var error = IntPtr.Zero;
            var result = secret_password_lookup_sync(
                _schema.Value, IntPtr.Zero, ref error, KeyAttribute, SecretKeys.Prefix + ":probe", IntPtr.Zero);

            if (result != IntPtr.Zero)
                secret_password_free(result);

            if (error != IntPtr.Zero)
            {
                g_error_free(error);
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            // DllNotFoundException, EntryPointNotFoundException — no libsecret here.
            return false;
        }
    }

    public ValueTask<bool> SetAsync(string key, string secret, CancellationToken ct = default) =>
        Run(() =>
        {
            var error = IntPtr.Zero;
            var stored = secret_password_store_sync(
                _schema.Value,
                collection: null,                            // null = the user's default collection
                label: SecretKeys.Describe(key),
                password: secret,
                cancellable: IntPtr.Zero,
                error: ref error,
                KeyAttribute, key, IntPtr.Zero);

            if (error == IntPtr.Zero)
                return stored;

            // The message can name the collection or the reason it is locked; it never contains the
            // secret, but it is not worth logging either — the caller decides what to show.
            g_error_free(error);
            return false;
        }, ct);

    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
        Run(() =>
        {
            var error = IntPtr.Zero;
            var result = secret_password_lookup_sync(
                _schema.Value, IntPtr.Zero, ref error, KeyAttribute, key, IntPtr.Zero);

            if (error != IntPtr.Zero)
            {
                g_error_free(error);
                return null;
            }

            if (result == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUTF8(result);
            }
            finally
            {
                // Frees with the allocator libsecret used, and wipes the memory first — which is the
                // reason not to hand the pointer to anything else before getting here.
                secret_password_free(result);
            }
        }, ct);

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default) =>
        await Run(() =>
        {
            var error = IntPtr.Zero;
            var cleared = secret_password_clear_sync(
                _schema.Value, IntPtr.Zero, ref error, KeyAttribute, key, IntPtr.Zero);

            if (error != IntPtr.Zero)
                g_error_free(error);

            return cleared;
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// Off the calling thread: these are the <c>_sync</c> entry points, and a locked keyring makes them
    /// block on the user answering an unlock prompt. On the UI thread that is a frozen window.
    /// </summary>
    private static async ValueTask<T> Run<T>(Func<T> work, CancellationToken ct) =>
        await Task.Run(work, ct).ConfigureAwait(false);

    // ── libsecret ───────────────────────────────────────────────────────────

    /// <summary>
    /// A schema describing the one attribute used. Allocated once and never freed: libsecret expects a
    /// schema to outlive the calls that reference it, and there is exactly one for the process.
    /// </summary>
    private static IntPtr CreateSchema() =>
        secret_schema_new(
            SecretKeys.Prefix, SecretSchemaFlags.None,
            KeyAttribute, SecretSchemaAttributeType.String,
            IntPtr.Zero);

    private enum SecretSchemaFlags
    {
        None = 0,
    }

    private enum SecretSchemaAttributeType
    {
        String = 0,
    }

    // Every string is marshalled as UTF-8 explicitly. glib takes UTF-8, and leaving it to the platform
    // default would mangle a label or a key with anything outside ASCII in it.
    [DllImport(Library)]
    private static extern IntPtr secret_schema_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, SecretSchemaFlags flags,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute, SecretSchemaAttributeType type,
        IntPtr terminator);

    [DllImport(Library)]
    private static extern bool secret_password_store_sync(
        IntPtr schema,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable, ref IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        IntPtr terminator);

    [DllImport(Library)]
    private static extern IntPtr secret_password_lookup_sync(
        IntPtr schema, IntPtr cancellable, ref IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        IntPtr terminator);

    [DllImport(Library)]
    private static extern bool secret_password_clear_sync(
        IntPtr schema, IntPtr cancellable, ref IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        IntPtr terminator);

    [DllImport(Library)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport("libgobject-2.0.so.0")]
    private static extern void g_error_free(IntPtr error);
}
