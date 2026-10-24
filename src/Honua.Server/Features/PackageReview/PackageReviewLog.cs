// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.PackageReview;

internal static partial class PackageReviewLog
{
    [LoggerMessage(10810, LogLevel.Information, "Package review completed: Family={PackageFamily}, Status={Status}, FindingCount={FindingCount}")]
    public static partial void PackageReviewCompleted(
        ILogger logger,
        string packageFamily,
        string status,
        int findingCount);

    [LoggerMessage(10811, LogLevel.Error, "Package review failed.")]
    public static partial void PackageReviewFailed(ILogger logger, Exception exception);
}
