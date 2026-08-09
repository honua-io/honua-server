// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Aws.Features.Licensing;

internal static partial class AwsLicenseLog
{
    [LoggerMessage(
        EventId = 10100,
        Level = LogLevel.Warning,
        Message = "AWS Secrets Manager license secret resolved to an empty value.")]
    public static partial void SecretEmpty(ILogger logger);

    [LoggerMessage(
        EventId = 10101,
        Level = LogLevel.Warning,
        Message = "Failed to fetch license envelope from AWS Secrets Manager; the host will fall back to Community. reason={Reason}")]
    public static partial void SecretFetchFailed(ILogger logger, string reason);
}
