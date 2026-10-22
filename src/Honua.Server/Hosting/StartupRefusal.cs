// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Hosting;

/// <summary>
/// Leaves the process with a plain status when startup is refused before the host is built.
/// </summary>
/// <remarks>
/// An exception that escapes the entry point aborts the runtime, and on the AOT image native
/// finalizers then run after their modules unload, which ends the process with a segmentation
/// fault instead of the refusal. A configuration refusal that is raised while the builder is still
/// being composed has no host to dispose, so it reports the message and returns from the entry point.
/// </remarks>
internal static class StartupRefusal
{
    /// <summary>Process status for a refused boot.</summary>
    internal const int ExitCode = GatedBootShutdown.RefusalExitCode;

    /// <summary>Records <see cref="ExitCode"/> and writes <paramref name="message"/>.</summary>
    /// <param name="message">Operator-facing reason, free of credentials.</param>
    internal static void Exit(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        Environment.ExitCode = ExitCode;
        Console.Error.WriteLine(message);

        // codeql[cs/call-to-gc]: refused boot is exiting and native modules are about to unload
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
