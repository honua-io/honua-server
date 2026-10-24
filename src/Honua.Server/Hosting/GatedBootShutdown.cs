// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Hosting;

/// <summary>
/// Stops a host whose boot a terminal migration refusal has rejected.
/// </summary>
/// <remarks>
/// The runner reports a contract-gate refusal as an <see cref="InvalidOperationException"/> and
/// returns it on the migration result. A schema-floor refusal is a
/// <see cref="DatabaseSchemaFloorException"/>. Letting either escape the entry point aborts the
/// runtime, and native finalizers (GDAL, PROJ, SpatiaLite, Npgsql) then run after their modules
/// are unloaded, which terminates the process with a segmentation fault instead of the refusal
/// status.
/// </remarks>
internal static class GatedBootShutdown
{
    /// <summary>
    /// Stable prefix of the migration safety gate's refusal. The runner does not use a dedicated
    /// exception type, so the entry point recognizes the refusal by this prefix.
    /// </summary>
    internal const string ContractGateRefusalPrefix = "Migration safety gate blocked";

    /// <summary>
    /// Stable prefix of the PostGIS preflight's terminal refusal (an incompatible or unparseable
    /// database configuration). Startup throws it from the preflight, which leaves the entry point
    /// through an unhandled exception unless the entry point stops the host itself.
    /// </summary>
    internal const string PreflightRefusalPrefix = "PostGIS preflight check failed";

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
            && exception is not DatabaseSchemaFloorException
            && exception.Message.StartsWith(ContractGateRefusalPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="exception"/> is a schema-floor refusal that must stop the host
    /// instead of aborting the runtime. Migrate-only keeps its own outcome for this exception;
    /// <see cref="IsContractGateRefusal"/> stays the contract-approval gate.
    /// </summary>
    /// <param name="exception">Failure reported by the schema guard.</param>
    /// <returns>Whether boot must stop with <see cref="RefusalExitCode"/>.</returns>
    internal static bool IsSchemaFloorRefusal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is DatabaseSchemaFloorException;
    }

    /// <summary>
    /// True when <paramref name="exception"/> is the PostGIS preflight's terminal refusal.
    /// </summary>
    /// <param name="exception">Failure thrown by the startup preflight.</param>
    /// <returns>Whether boot must stop with <see cref="RefusalExitCode"/>.</returns>
    internal static bool IsPreflightRefusal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is InvalidOperationException
            && exception.Message.StartsWith(PreflightRefusalPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the host serves through the in-process test server. A test host runs the entry
    /// point on a worker thread and needs the refusal as an exception; it has no process to exit.
    /// </summary>
    /// <param name="app">Host that has been built.</param>
    /// <returns>Whether the host is an in-process test server.</returns>
    internal static bool IsInProcessTestServer(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return string.Equals(
            app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?.GetType().FullName,
            "Microsoft.AspNetCore.TestHost.TestServer",
            StringComparison.Ordinal);
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
