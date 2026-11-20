// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core;

/// <summary>
/// An expected failure whose message is for the player (the Rust launcher's <c>Err(String)</c>):
/// a file that cannot be written, a game that must be closed first, a download that does not
/// verify. <see cref="ILauncher"/> never lets one out; game targets throw it from install and
/// session writes.
/// </summary>
public sealed class LauncherException : Exception
{
    public LauncherException()
    {
    }

    public LauncherException(string message)
        : base(message)
    {
    }

    public LauncherException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
