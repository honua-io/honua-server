// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Cloud.Aws.Features.Licensing;

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
        Message = "Failed to fetch license envelope from AWS Secrets Manager; paid-tier startup will be refused if no valid license can be loaded. reason={Reason}")]
    public static partial void SecretFetchFailed(ILogger logger, string reason);
}
