namespace Kontena.Sdk.Tooling;

/// <summary>The package managers Kontena knows how to phrase an install command for.</summary>
public enum PackageManager
{
    /// <summary>macOS and Linux — Homebrew.</summary>
    Homebrew,

    /// <summary>Windows — winget, present on current Windows installs.</summary>
    Winget,

    /// <summary>Windows — Scoop.</summary>
    Scoop,

    /// <summary>Debian, Ubuntu and derivatives.</summary>
    Apt,

    /// <summary>Fedora, RHEL and derivatives.</summary>
    Dnf,

    /// <summary>Arch and derivatives.</summary>
    Pacman,

    /// <summary>No package manager: download a release binary and put it on PATH yourself.</summary>
    Manual,
}
