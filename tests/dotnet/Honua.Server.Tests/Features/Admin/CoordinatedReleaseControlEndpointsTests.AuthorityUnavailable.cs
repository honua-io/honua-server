// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class CoordinatedReleaseControlEndpointsTests
{
    private readonly InMemorySecurityTokenStore _sharedSessions = new();
    private bool _sessionReadsUnavailable;

    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    [InlineData("shared-session")]
    [InlineData("base-url")]
    public async Task ApproveGate_AuthorityUnavailable_Returns503WithoutChangingTheGate(string outage)
    {
        var operation = await PrepareHumanGateAsync(CoordinatedReleaseStep.ContainerRollout);
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity);
        var review = await human.OpenGateReviewAsync(operation.OperationId, "container");
        if (outage == "shared-session") _sessionReadsUnavailable = true;
        else _fixture.Services.GetRequiredService<IConfiguration>()["Public:BaseUrl"] = null;

        using var approval = await human.SubmitGateReviewAsync(operation.OperationId, "container", review);
        using var reopen = await human.Client.GetAsync(
            $"/api/v1/admin/metadata/coordinated-releases/operations/{operation.OperationId}/approval-review/container");

        approval.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        reopen.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(operation);
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    private ISecurityTokenStore CreateSessionTokens()
    {
        var tokens = Substitute.For<ISecurityTokenStore>();
        tokens.FindAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<CancellationToken>()).Returns(call => _sessionReadsUnavailable
            ? Task.FromException<StoredSecurityToken?>(new InvalidOperationException("Shared sessions unavailable."))
            : _sharedSessions.FindAsync(call.Arg<SecurityTokenKey>(), call.Arg<CancellationToken>()));
        tokens.StoreAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => _sharedSessions.StoreAsync(call.Arg<SecurityTokenKey>(), call.Arg<ReadOnlyMemory<byte>>(),
                call.Arg<TimeSpan>(), call.Arg<CancellationToken>()));
        tokens.RevokeAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<CancellationToken>())
            .Returns(call => _sharedSessions.RevokeAsync(call.Arg<SecurityTokenKey>(), call.Arg<CancellationToken>()));
        return tokens;
    }
}
