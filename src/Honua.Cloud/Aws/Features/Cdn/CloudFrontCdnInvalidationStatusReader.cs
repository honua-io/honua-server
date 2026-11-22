// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.CloudFront.Model;
using Amazon.Runtime;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Cloud.Aws.Features.Cdn;

internal sealed class CloudFrontCdnInvalidationStatusReader(
    Func<GetInvalidationRequest, CancellationToken, Task<GetInvalidationResponse>> getInvalidation,
    string distributionId) : ICdnInvalidationStatusReader
{
    public async Task<CdnInvalidationStatusResult> ReadAsync(CdnInvalidationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.ResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.InvalidationId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(receipt.ResourceId, distributionId, StringComparison.Ordinal))
        {
            return new(CdnInvalidationStatus.Failed, "ResourceMismatch");
        }

        try
        {
            var response = await getInvalidation(new GetInvalidationRequest
            {
                DistributionId = receipt.ResourceId,
                Id = receipt.InvalidationId
            }, cancellationToken).ConfigureAwait(false);
            var invalidation = response?.Invalidation;
            if (invalidation is null || !string.Equals(invalidation.Id, receipt.InvalidationId, StringComparison.Ordinal))
            {
                return new(CdnInvalidationStatus.Failed, "InvalidResponse");
            }

            return invalidation.Status switch
            {
                "Completed" => new(CdnInvalidationStatus.Completed),
                "InProgress" => new(CdnInvalidationStatus.Pending),
                _ => new(CdnInvalidationStatus.Failed, "UnexpectedStatus")
            };
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or TimeoutException)
        {
            return new(CdnInvalidationStatus.Failed,
                (exception as AmazonServiceException)?.ErrorCode ?? exception.GetType().Name);
        }
    }
}
