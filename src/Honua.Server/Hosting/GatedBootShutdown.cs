// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Hosting;

/// <summary>
/// Stops a host whose boot the migration safety gate has refused.
/// </summary>
/// <remarks>
/// The runner reports that refusal as an <see cref="InvalidOperationException"/> and returns it
/// on the migration result. Letting the exception escape the entry point aborts the runtime, and
/// native finalizers (GDAL, PROJ, SpatiaLite, Npgsql) then run after their modules are unloaded,
/// which terminates the process with a segmentation fault instead of the refusal status.
/// </remarks>
internal static class GatedBootShutdown
{
    /// <summary>
    /// Stable prefix of the migration safety gate's refusal. The runner does not use a dedicated
    /// exception type, so the entry point recognizes the refusal by this prefix.
    /// </summary>
    internal const string ContractGateRefusalPrefix = "Migration safety gate blocked";

    /// <summary>
    /// Process status for a boot the migration safety gate refused. Operators and orchestrators
    /// treat any non-zero status as a failed start. An abort or segmentation fault is not this status.
    /// </summary>
    internal const int RefusalExitCode = 1;

    /// <summary>
    /// True when <paramref name="exception"/> is the migration safety gate's terminal refusal.
    /// </summary>
    /// <param name="exception">Failure reported by the migration runner.</param>
    /// <returns>Whether boot must stop with <see cref="RefusalExitCode"/>.</returns>
    internal static bool IsContractGateRefusal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is InvalidOperationException
            && exception.Message.StartsWith(ContractGateRefusalPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records <see cref="RefusalExitCode"/>, writes <paramref name="refusalMessage"/>, and disposes
    /// <paramref name="app"/> once while native modules are still loaded.
    /// </summary>
    /// <param name="app">Host that has been built but must not continue to run.</param>
    /// <param name="refusalMessage">Gate message already logged for the operator.</param>
    internal static async Task StopRefusedHostAsync(WebApplication app, string refusalMessage)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(refusalMessage);

        Environment.ExitCode = RefusalExitCode;
        Console.Error.WriteLine(refusalMessage);

        try
        {
            await app.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine(
                "Gated boot shutdown did not complete: " + ex.GetType().Name + ": " + ex.Message);
        }

        // Instance finalizers of native handles must run before the runtime unloads their modules.
        // codeql[cs/call-to-gc]: gated boot is exiting and native modules are about to unload
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // codeql[cs/call-to-gc]: second collect reclaims objects finalized during gated boot exit
        GC.Collect();
    }
}
