// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Honua.Cloud.Aws.Features.Cdn;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Aws.UnitTests;

public sealed class CloudFrontCdnInvalidatorTests
{
    private const string DistributionId = "E2EXAMPLE";

    [UnitTest]
    public async Task InvalidateAsync_SendsDistinctPathsToTheConfiguredDistribution()
    {
        var requests = new List<CreateInvalidationRequest>();
        var invalidator = Invalidator(request => Accept(requests, request));

        var result = await invalidator.InvalidateAsync(["/rest/services/parcels/*", "/tiles/3/*", "/tiles/3/*"]);

        var request = Assert.Single(requests);
        Assert.Equal(DistributionId, request.DistributionId);
        Assert.Equal(["/rest/services/parcels/*", "/tiles/3/*"], request.InvalidationBatch.Paths.Items);
        Assert.Equal(2, request.InvalidationBatch.Paths.Quantity);
        Assert.Equal(CdnInvalidationOutcome.Submitted, result.Outcome);
    }

    [UnitTest]
    public async Task InvalidateAsync_MorePathsThanOneRequestAllows_AreBatched()
    {
        var requests = new List<CreateInvalidationRequest>();
        var invalidator = Invalidator(request => Accept(requests, request));
        var paths = Enumerable.Range(0, CloudFrontCdnInvalidator.MaxPathsPerRequest + 1)
            .Select(index => $"/tiles/{index}/*")
            .ToArray();

        await invalidator.InvalidateAsync(paths);

        Assert.Equal([CloudFrontCdnInvalidator.MaxPathsPerRequest, 1], requests.Select(request => request.InvalidationBatch.Paths.Items.Count));
        Assert.Equal(2, requests.Select(request => request.InvalidationBatch.CallerReference).Distinct().Count());
    }

    [UnitTest]
    public async Task InvalidateAsync_CloudFrontRefuses_ReturnsTheFailureInsteadOfThrowing()
    {
        var invalidator = Invalidator(_ => throw new AmazonCloudFrontException("denied") { ErrorCode = "AccessDenied" });

        var result = await invalidator.InvalidateAsync(["/*"]);

        Assert.True(result.Failed);
        Assert.Equal("AccessDenied", result.Detail);
    }

    private static CloudFrontCdnInvalidator Invalidator(Func<CreateInvalidationRequest, CreateInvalidationResponse> respond)
        => new(
            (request, _) => Task.FromResult(respond(request)),
            DistributionId,
            NullLogger<CloudFrontCdnInvalidator>.Instance);

    private static CreateInvalidationResponse Accept(List<CreateInvalidationRequest> requests, CreateInvalidationRequest request)
    {
        requests.Add(request);
        return new CreateInvalidationResponse { Invalidation = new Invalidation { Id = $"I{requests.Count}" } };
    }
}
