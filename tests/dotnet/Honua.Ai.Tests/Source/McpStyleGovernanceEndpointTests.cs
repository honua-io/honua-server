// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class McpStyleGovernanceEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/operations/style.apply-preset/submit")]
    public async Task RestStyleOperation_DeniedPublishGrant_CannotBypassMcpAuthorization()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.EnsureCallerAuthorizedAsync(Arg.Any<ClaimsPrincipal>(), OperatorResourceType.PublishedService,
                OperatorOperation.Publish, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new GeoprocessingAuthorizationException(requiresAuthentication: false)));
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
        var fixture = new WebAppFixture()
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
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            await jobs.Received(1).EnsureCallerAuthorizedAsync(Arg.Any<ClaimsPrincipal>(),
                OperatorResourceType.PublishedService, OperatorOperation.Publish, Arg.Any<CancellationToken>());
            await invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
