using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Kontena.App.Services;

/// <summary>
/// Windows: Credential Manager, through the advapi32 <c>Cred*</c> APIs (KON-52).
/// <para>
/// Ported from SQL Explorer's <c>WindowsCredentialStore</c>, which uses the same three calls. Adapted to
/// this contract: a refusal is reported rather than thrown, and the calls run off the caller's thread.
/// The legacy-prefix migration from that codebase is left out — it exists for a rebrand Kontena never
/// had.
/// </para>
/// <para>
/// Entries are per-user and appear in Credential Manager under a <c>Kontena:</c> prefix, so the user can
/// find and remove them without Kontena's help.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ISecretStore
{
    private const string TargetPrefix = "Kontena:";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    /// <summary>
    /// Whether advapi32 answers. On Windows it always should; this is here so a host that somehow cannot
    /// reach it reports honestly instead of throwing on the first save.
    /// </summary>
    public bool IsAvailable => _available ??= Probe();

    private bool? _available;

    private static bool Probe()
    {
        try
        {
            // A read of something that does not exist: fails with "not found", which is an answer, and
            // proves the entry point resolves.
            if (CredRead(TargetPrefix + "probe", CredTypeGeneric, 0, out var handle) && handle != IntPtr.Zero)
                CredFree(handle);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask<bool> SetAsync(string key, string secret, CancellationToken ct = default) =>
        Run(() =>
        {
            // Unicode, because that is what CredReadW hands back and what Get below decodes.
            var blob = Encoding.Unicode.GetBytes(secret);
            var blobPtr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = TargetPrefix + key,
                    Comment = SecretKeys.Describe(key),
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CredPersistLocalMachine,
                    UserName = Environment.UserName,
                };

                return CredWrite(ref credential, 0);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                // Zero the copy before releasing it: the secret is in unmanaged memory that nothing else
                // will wipe.
                for (var i = 0; i < blob.Length; i++)
                    Marshal.WriteByte(blobPtr, i, 0);

                Marshal.FreeHGlobal(blobPtr);
                Array.Clear(blob);
            }
        }, ct);

    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
        Run<string?>(() =>
        {
            if (!CredRead(TargetPrefix + key, CredTypeGeneric, 0, out var handle))
                return null;

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(handle);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                    return string.Empty;

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                try
                {
                    return Encoding.Unicode.GetString(bytes);
                }
                finally
                {
                    Array.Clear(bytes);
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                CredFree(handle);
            }
        }, ct);

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default) =>
        await Run(() =>
        {
            try
            {
                // False means "was not there", which is the outcome the caller asked for anyway.
                return CredDelete(TargetPrefix + key, CredTypeGeneric, 0);
            }
            catch (Exception)
            {
                return false;
            }
        }, ct).ConfigureAwait(false);

    private static async ValueTask<T> Run<T>(Func<T> work, CancellationToken ct) =>
        await Task.Run(work, ct).ConfigureAwait(false);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
