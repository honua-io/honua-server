// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

internal static partial class InputValidationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Input validation rejected request: {Error}")]
    public static partial void InputRejected(ILogger logger, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malicious input detected: {Error} from {IpAddress}")]
    public static partial void MaliciousInputDetected(ILogger logger, string? error, string? ipAddress);
}
