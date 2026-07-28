namespace Kontena.App.ViewModels;

/// <summary>
/// A dialog with one obvious action, so Enter can do it (KON-172).
/// <para>
/// Opt-in rather than assumed. Enter meaning "the primary button" is only safe where there is one
/// primary button and no multi-line field to hijack — a confirm has both properties, a YAML editor
/// has neither. A dialog that does not implement this simply does not answer Enter, which is the
/// behaviour it had before; nothing is left looking wired that is not.
/// </para>
/// </summary>
public interface IPrimaryAction
{
    /// <summary>Whether the primary action can run right now — the same guard its button uses.</summary>
    bool CanInvokePrimary { get; }

    /// <summary>Run it.</summary>
    void InvokePrimary();
}
