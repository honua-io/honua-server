// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

// Span event attributes cannot be rewritten after they are added, and until a policy is installed nothing is exported.
public static class TelemetryExceptionDetailPolicy
{
    private static volatile Func<string?, string?> _exportableMessage = static _ => null;

    public static void Configure(Func<string?, string?> exportableMessage)
    {
        ArgumentNullException.ThrowIfNull(exportableMessage);
        _exportableMessage = exportableMessage;
    }

    public static string? ExportableMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = _exportableMessage(exception.Message);
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }
}
