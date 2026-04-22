// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Security;

internal static partial class InputValidationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Malicious input detected: {Error} from {IpAddress}")]
    public static partial void MaliciousInputDetected(ILogger logger, string? error, string? ipAddress);
}
