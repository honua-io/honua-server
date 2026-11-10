// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Cloud.Aws.Features.Cdn;

internal sealed partial class CloudFrontCdnInvalidator(
    IAmazonCloudFront cloudFront,
    string distributionId,
    ILogger<CloudFrontCdnInvalidator> logger) : ICdnInvalidator
{
    // CloudFront accepts at most this many paths in one invalidation request.
    internal const int MaxPathsPerRequest = 3000;

    public async Task<CdnInvalidationResult> InvalidateAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var distinctPaths = paths.Distinct(StringComparer.Ordinal).ToArray();
        var invalidationIds = new List<string>();
        try
        {
            foreach (var batch in distinctPaths.Chunk(MaxPathsPerRequest))
            {
                var response = await cloudFront
                    .CreateInvalidationAsync(RequestFor(batch), cancellationToken)
                    .ConfigureAwait(false);
                invalidationIds.Add(response.Invalidation.Id);
            }
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or TimeoutException)
        {
            var errorCode = (exception as AmazonServiceException)?.ErrorCode ?? exception.GetType().Name;
            Log.InvalidationFailed(logger, exception, distributionId, distinctPaths.Length, errorCode);
            return new CdnInvalidationResult(CdnInvalidationOutcome.Failed, errorCode);
        }

        Log.InvalidationSubmitted(logger, distributionId, distinctPaths.Length);
        return new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, string.Join(',', invalidationIds));
    }

    private CreateInvalidationRequest RequestFor(string[] batch) => new()
    {
        DistributionId = distributionId,
        InvalidationBatch = new InvalidationBatch
        {
            CallerReference = Guid.NewGuid().ToString("N"),
            Paths = new Paths { Quantity = batch.Length, Items = [.. batch] },
        },
    };

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 10300,
            Level = LogLevel.Information,
            Message = "CloudFront invalidation submitted. distribution={DistributionId} paths={PathCount}")]
        public static partial void InvalidationSubmitted(ILogger logger, string distributionId, int pathCount);

        [LoggerMessage(
            EventId = 10301,
            Level = LogLevel.Error,
            Message = "CloudFront invalidation failed. distribution={DistributionId} paths={PathCount} error={ErrorCode}")]
        public static partial void InvalidationFailed(
            ILogger logger, Exception exception, string distributionId, int pathCount, string errorCode);
    }
}
