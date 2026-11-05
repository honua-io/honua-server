// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Hosting;

internal static class GatedBootShutdown
{
    internal const string ContractGateRefusalPrefix = "Migration safety gate blocked";

    internal const string PreflightRefusalPrefix = "PostGIS preflight check failed";

    internal const int RefusalExitCode = 1;

    internal static bool IsContractGateRefusal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is InvalidOperationException
            && exception is not DatabaseSchemaFloorException
            && exception.Message.StartsWith(ContractGateRefusalPrefix, StringComparison.Ordinal);
    }

    internal static bool IsSchemaFloorRefusal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is DatabaseSchemaFloorException;
    }

    // Letting this provider exception escape aborts native AOT instead of returning a refusal.
    internal const string AttachmentBindingRefusalTypeName =
        "Honua.Db.Postgres.Features.Attachments.AttachmentSchemaBindingException";

    internal static bool IsAttachmentBindingRefusal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return string.Equals(
            exception.GetType().FullName,
            AttachmentBindingRefusalTypeName,
            StringComparison.Ordinal);
    }

    internal static bool IsPreflightRefusal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is InvalidOperationException
            && exception.Message.StartsWith(PreflightRefusalPrefix, StringComparison.Ordinal);
    }

    // A test host needs the refusal as an exception, because it has no process to exit.
    internal static bool IsInProcessTestServer(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return string.Equals(
            app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?.GetType().FullName,
            "Microsoft.AspNetCore.TestHost.TestServer",
            StringComparison.Ordinal);
    }

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
