// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Honua.Cloud.Aws.Features.Cdn;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit.Attributes;

namespace Honua.Aws.UnitTests;

public sealed class CloudFrontCdnInvalidationStatusReaderTests
{
    private const string DistributionId = "E2EXAMPLE";
    private const string InvalidationId = "I1EXAMPLE";

    [UnitTest]
    public async Task ReadAsync_CompletedOnAnotherTask_QueriesPersistedIdAndForwardsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        GetInvalidationRequest? submitted = null;
        CancellationToken forwarded = default;
        var reader = new CloudFrontCdnInvalidationStatusReader((request, token) =>
        {
            submitted = request;
            forwarded = token;
            return Task.FromResult(Response("Completed"));
        }, DistributionId);

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId), cancellation.Token);

        Assert.NotNull(submitted);
        Assert.Equal(DistributionId, submitted.DistributionId);
        Assert.Equal(InvalidationId, submitted.Id);
        Assert.Equal(cancellation.Token, forwarded);
        Assert.Equal(CdnInvalidationStatus.Completed, result.Status);
    }

    [UnitTheory]
    [InlineData("InProgress", CdnInvalidationStatus.Pending)]
    [InlineData("Completed", CdnInvalidationStatus.Completed)]
    [InlineData("completed", CdnInvalidationStatus.Failed)]
    [InlineData("Unknown", CdnInvalidationStatus.Failed)]
    [InlineData("", CdnInvalidationStatus.Failed)]
    [InlineData(null, CdnInvalidationStatus.Failed)]
    public async Task ReadAsync_ProviderStatus_CompletesOnlyKnownCompletedStatus(string? status, CdnInvalidationStatus expected)
    {
        var reader = Reader(() => Response(status));

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId));

        Assert.Equal(expected, result.Status);
    }

    [UnitTest]
    public async Task ReadAsync_ResponseForDifferentId_DoesNotComplete()
    {
        var reader = Reader(() => new GetInvalidationResponse
        {
            Invalidation = new Invalidation { Id = "IOTHER", Status = "Completed" }
        });

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId));

        Assert.Equal(CdnInvalidationStatus.Failed, result.Status);
    }

    [UnitTest]
    public async Task ReadAsync_MissingInvalidation_DoesNotComplete()
    {
        var reader = Reader(() => new GetInvalidationResponse());

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId));

        Assert.Equal(CdnInvalidationStatus.Failed, result.Status);
    }

    [UnitTheory]
    [InlineData("AccessDenied")]
    [InlineData("NoSuchInvalidation")]
    [InlineData("NoSuchDistribution")]
    [InlineData("Throttling")]
    public async Task ReadAsync_ProviderRefuses_DoesNotTreatFailureAsCompletion(string errorCode)
    {
        var reader = Reader(() => throw new AmazonCloudFrontException("provider detail") { ErrorCode = errorCode });

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId));

        Assert.Equal(CdnInvalidationStatus.Failed, result.Status);
        Assert.Equal(errorCode, result.Detail);
    }

    [UnitTest]
    public async Task ReadAsync_TransportUnavailable_ReturnsFailure()
    {
        var reader = Reader(() => throw new HttpRequestException("transport detail"));

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId));

        Assert.Equal(CdnInvalidationStatus.Failed, result.Status);
        Assert.Equal(nameof(HttpRequestException), result.Detail);
    }

    [UnitTest]
    public async Task ReadAsync_Canceled_DoesNotConfirmCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new CloudFrontCdnInvalidationStatusReader((_, token) =>
            Task.FromCanceled<GetInvalidationResponse>(token), DistributionId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new(DistributionId, InvalidationId), cancellation.Token));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task ReadAsync_InvalidId_RefusesBeforeProvider(string? invalidationId)
    {
        var reader = Reader(() => throw new InvalidOperationException("provider must not be called"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => reader.ReadAsync(new(DistributionId, invalidationId!)));
    }

    [UnitTest]
    public async Task ReadAsync_ReceiptForDifferentDistribution_RefusesBeforeProvider()
    {
        var reader = Reader(() => throw new InvalidOperationException("provider must not be called"));

        var result = await reader.ReadAsync(new("EOTHER", InvalidationId));

        Assert.Equal(CdnInvalidationStatus.Failed, result.Status);
        Assert.Equal("ResourceMismatch", result.Detail);
    }

    [UnitTest]
    public async Task ReadAsync_NullReceipt_RefusesBeforeProvider()
    {
        var reader = Reader(() => throw new InvalidOperationException("provider must not be called"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.ReadAsync(null!));
    }

    [UnitTest]
    public async Task ReadAsync_MissingResponse_DoesNotComplete()
    {
        var reader = Reader(() => null!);

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId));

        Assert.Equal(CdnInvalidationStatus.Failed, result.Status);
    }

    [UnitTest]
    public async Task ReadAsync_ProviderTimesOut_ReturnsFailure()
    {
        var reader = Reader(() => throw new TimeoutException("provider detail"));

        var result = await reader.ReadAsync(new(DistributionId, InvalidationId));

        Assert.Equal(CdnInvalidationStatus.Failed, result.Status);
        Assert.Equal(nameof(TimeoutException), result.Detail);
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task ReadAsync_InvalidResource_RefusesBeforeProvider(string? resourceId)
    {
        var reader = Reader(() => throw new InvalidOperationException("provider must not be called"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => reader.ReadAsync(new(resourceId!, InvalidationId)));
    }

    private static CloudFrontCdnInvalidationStatusReader Reader(Func<GetInvalidationResponse> response)
        => new((_, _) => Task.FromResult(response()), DistributionId);

    private static GetInvalidationResponse Response(string? status)
        => new() { Invalidation = new Invalidation { Id = InvalidationId, Status = status } };
}
