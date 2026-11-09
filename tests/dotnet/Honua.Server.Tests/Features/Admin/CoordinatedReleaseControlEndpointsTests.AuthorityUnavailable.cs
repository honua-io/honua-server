// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class CoordinatedReleaseControlEndpointsTests
{
    private readonly MemoryDistributedCache _sharedSessions = new(Options.Create(new MemoryDistributedCacheOptions()));
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

    private IDistributedCache CreateSessionCache()
    {
        var cache = Substitute.For<IDistributedCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _sessionReadsUnavailable
            ? Task.FromException<byte[]?>(new InvalidOperationException("Shared sessions unavailable."))
            : _sharedSessions.GetAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        cache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => _sharedSessions.SetAsync(call.Arg<string>(), call.Arg<byte[]>(),
                call.Arg<DistributedCacheEntryOptions>(), call.Arg<CancellationToken>()));
        cache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _sharedSessions.RemoveAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        return cache;
    }
}
