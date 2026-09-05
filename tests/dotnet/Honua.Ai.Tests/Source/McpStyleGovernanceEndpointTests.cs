// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class McpStyleGovernanceEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/operations/style.apply-preset/submit")]
    public async Task RestStyleOperation_EnforcesPublishGrantAndSuppliesTrustedTier(bool publishAllowed)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        if (!publishAllowed)
        {
            jobs.EnsureCallerAuthorizedAsync(Arg.Any<ClaimsPrincipal>(), OperatorResourceType.PublishedService,
                OperatorOperation.Publish, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new GeoprocessingAuthorizationException(requiresAuthentication: false)));
        }
        var invoker = Substitute.For<IOperationInvoker>();
        invoker.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(new OperationHandle
            {
                OperationId = "style.apply-preset",
                OperationInstanceId = "unexpected-style-write",
                CorrelationId = "style-grant-test",
                Status = OperationHandleStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        var license = new TestLicenseEntitlementService(HonuaEdition.Pro);
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(license)
            .ReplaceService<IGeoprocessingJobService>(jobs)
            .ReplaceService<IOperationInvoker>(invoker);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.PostAsJsonAsync("/api/v1/operations/style.apply-preset/submit", new
            {
                parameters = new Dictionary<string, string>
                {
                    ["serviceId"] = WebAppFixture.TestServiceId,
                    ["layerId"] = "0",
                    ["styleId"] = "restricted-preset",
                },
            });
            response.StatusCode.Should().Be(publishAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
            await jobs.Received(1).EnsureCallerAuthorizedAsync(Arg.Any<ClaimsPrincipal>(),
                OperatorResourceType.PublishedService, OperatorOperation.Publish, Arg.Any<CancellationToken>());
            if (publishAllowed)
            {
                await invoker.Received(1).SubmitAsync(Arg.Any<OperationRequest>(),
                    Arg.Is<OperationPolicyContext>(context => context.Tier == "pro" && context.AuthorizationOutcome == "authorized"),
                    Arg.Any<CancellationToken>());
            }
            else
            {
                await invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("not-a-layer")]
    [InlineData("2147483648")]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/operations/style.apply-preset/validate")]
    public async Task RestStyleValidation_MalformedLayerId_ReturnsBadRequest(string layerId)
    {
        var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.PostAsJsonAsync("/api/v1/operations/style.apply-preset/validate", new
            {
                parameters = new Dictionary<string, string>
                {
                    ["serviceId"] = WebAppFixture.TestServiceId,
                    ["layerId"] = layerId,
                    ["styleId"] = "validation-preset",
                },
            });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("32-bit integer");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

}
