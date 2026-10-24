// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Middleware;

internal static partial class HostValidationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected request with untrusted host header '{Host}' for path '{Path}'.")]
    public static partial void RejectedUntrustedHost(ILogger logger, string? host, string? path);
}
