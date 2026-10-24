// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Licensing;

internal static partial class AzureLicenseLog
{
    [LoggerMessage(
        EventId = 10110,
        Level = LogLevel.Warning,
        Message = "Azure Key Vault license secret resolved to an empty value.")]
    public static partial void SecretEmpty(ILogger logger);

    [LoggerMessage(
        EventId = 10111,
        Level = LogLevel.Warning,
        Message = "Failed to fetch license envelope from Azure Key Vault; paid-tier startup will be refused if no valid license can be loaded. reason={Reason}")]
    public static partial void SecretFetchFailed(ILogger logger, string reason);
}
