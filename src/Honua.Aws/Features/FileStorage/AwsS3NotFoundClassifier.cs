// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Amazon.S3;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.FileStorage;

/// <summary>
/// Recognizes AWS S3 "missing object" failures (<see cref="AmazonS3Exception"/>
/// with a 404 status or a <c>NoSuchKey</c> error code) so cloud-neutral
/// orchestrators (e.g. the PMTiles range proxy) can surface them as 404 rather
/// than 500. Registered as one of the <see cref="ICloudNotFoundClassifier"/>
/// implementations consumed by <c>PMTilesProxyService</c>.
/// </summary>
internal sealed class AwsS3NotFoundClassifier : ICloudNotFoundClassifier
{
    public bool IsNotFound(Exception exception) =>
        exception is AmazonS3Exception s3 &&
        (s3.StatusCode == HttpStatusCode.NotFound
            || string.Equals(s3.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase));
}
