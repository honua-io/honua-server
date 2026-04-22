// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static partial class LicenseEndpoints
{
    internal static partial class LicenseLog
    {
        [LoggerMessage(EventId = 4554, Level = LogLevel.Error, Message = "Failed to retrieve license status")]
        public static partial void RetrieveLicenseStatusFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4555, Level = LogLevel.Error, Message = "Failed to upload license")]
        public static partial void UploadLicenseFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4556, Level = LogLevel.Error, Message = "Failed to retrieve entitlements")]
        public static partial void RetrieveEntitlementsFailed(ILogger logger, Exception exception);
    }
}
