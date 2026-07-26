using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Kontena.App.Services;

/// <summary>
/// macOS: the login Keychain, through Security.framework's generic-password calls (KON-52).
/// <para>
/// Ported from SQL Explorer's <c>MacKeychainStore</c>. Adapted to this contract — refusals are reported
/// rather than thrown, and the calls run off the caller's thread, which matters here more than anywhere:
/// the first read of a keychain item can put up a system prompt asking the user to allow access.
/// </para>
/// <para>
/// These are the older <c>SecKeychain*</c> functions rather than the <c>SecItem*</c> family. Deliberate:
/// they take plain byte arrays where <c>SecItemAdd</c> takes a CoreFoundation dictionary, and for one
/// service with one account per key the simpler binding is the one with less to get wrong.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainStore : ISecretStore
{
    /// <summary>The service every entry is filed under; the key is the account within it.</summary>
    private const string Service = "app.kontena.Kontena";

    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>
    /// Whether Security.framework answers. A lookup of something absent returns
    /// <c>errSecItemNotFound</c>, which is an answer — what this is really testing is that the entry
    /// point resolves at all.
    /// </summary>
    public bool IsAvailable => _available ??= Probe();

    private bool? _available;

    private static bool Probe()
    {
        try
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(SecretKeys.Prefix + ":probe");

            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, out var data, out var item);

            if (status == 0)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero)
                    CFRelease(item);
            }

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
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(key);
            var password = Encoding.UTF8.GetBytes(secret);

            try
            {
                // Add fails on an existing item, so an existing one is modified instead. Two calls, not
                // delete-then-add: that would lose the secret if the second failed.
                var found = SecKeychainFindGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    out _, out _, out var item);

                if (found == 0 && item != IntPtr.Zero)
                {
                    var modified = SecKeychainItemModifyAttributesAndData(
                        item, IntPtr.Zero, (uint)password.Length, password);
                    CFRelease(item);
                    return modified == 0;
                }

                return SecKeychainAddGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    (uint)password.Length, password, out _) == 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                Array.Clear(password);
            }
        }, ct);

    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
        Run<string?>(() =>
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(key);

            try
            {
                var status = SecKeychainFindGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    out var length, out var data, out var item);

                if (status != 0)
                    return null;

                try
                {
                    var bytes = new byte[length];
                    Marshal.Copy(data, bytes, 0, (int)length);
                    try
                    {
                        return Encoding.UTF8.GetString(bytes);
                    }
                    finally
                    {
                        Array.Clear(bytes);
                    }
                }
                finally
                {
                    // Frees with the allocator the framework used; anything else corrupts the heap. The
                    // status is discarded on purpose: there is no recovery from a failed free.
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                    if (item != IntPtr.Zero)
                        CFRelease(item);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }, ct);

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default) =>
        await Run(() =>
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(key);

            try
            {
                var status = SecKeychainFindGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    out _, out var data, out var item);

                if (status != 0)
                    return false;                            // nothing there, which is what was wanted

                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item == IntPtr.Zero)
                    return false;

                var deleted = SecKeychainItemDelete(item) == 0;
                CFRelease(item);
                return deleted;
            }
            catch (Exception)
            {
                return false;
            }
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// Off the calling thread. The first access to a keychain item can raise a system prompt asking the
    /// user to allow it, and that blocks until they answer — on the UI thread, that is a frozen window.
    /// </summary>
    private static async ValueTask<T> Run<T>(Func<T> work, CancellationToken ct) =>
        await Task.Run(work, ct).ConfigureAwait(false);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
