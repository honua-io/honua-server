// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Middleware;

internal static partial class HostValidationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected request with untrusted host header '{Host}' for path '{Path}'.")]
    public static partial void RejectedUntrustedHost(ILogger logger, string? host, string? path);
}
