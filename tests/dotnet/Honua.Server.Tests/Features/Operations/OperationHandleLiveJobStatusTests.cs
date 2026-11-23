// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

// Hosts without the background reconciler answer handle reads from the stored envelope, so a
// queued layer-publish job must be read live through its executor or it stays Queued forever.
public sealed class OperationHandleLiveJobStatusTests
{
    private static readonly DateTimeOffset QueuedAt = new(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public async Task HandleRead_QueuedAdminLayerPublish_ReturnsLiveJobStatus()
    {
        var handle = QueuedHandle(AdminApiOperationExecutor.LayerPublishOperationId);
        var executor = Substitute.For<IOperationExecutor>();
        executor.OperationId.Returns(handle.OperationId);
        executor.GetStatusAsync(handle, Arg.Any<CancellationToken>()).Returns(OperationStatusProjection.FromHandle(handle) with
        {
            Status = OperationHandleStatus.Failed,
            Reason = "Layer publish exceeded its 1-second budget.",
        });

        var status = await ReadAsync(handle, executor);

        status.Status.Should().Be(OperationHandleStatus.Failed);
        status.Reason.Should().Be("Layer publish exceeded its 1-second budget.");
    }

    [UnitTest]
    public async Task HandleRead_QueuedOperationWithoutLiveJobStatus_ReturnsStoredHandle()
    {
        var handle = QueuedHandle("admin.test");
        var executor = Substitute.For<IOperationExecutor>();
        executor.OperationId.Returns(handle.OperationId);

        var status = await ReadAsync(handle, executor);

        status.Status.Should().Be(OperationHandleStatus.Queued);
        await executor.DidNotReceiveWithAnyArgs().GetStatusAsync(default!, default);
    }

    private static OperationHandle QueuedHandle(string operationId) => new()
    {
        OperationInstanceId = "handle-a",
        OperationId = operationId,
        CorrelationId = "correlation-a",
        Status = OperationHandleStatus.Queued,
        JobId = "job-170a8f64",
        CreatedAt = QueuedAt,
        UpdatedAt = QueuedAt,
    };

    private static async Task<OperationStatus> ReadAsync(OperationHandle handle, IOperationExecutor executor)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        using var services = new ServiceCollection().AddLogging().AddSingleton(authorization)
            .AddSingleton(executor).BuildServiceProvider();
        var store = Substitute.For<IOperationInstanceStore>();
        store.GetAsync(handle.OperationInstanceId, Arg.Any<CancellationToken>()).Returns(handle);
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Test")),
        };
        var handler = typeof(OperationsEndpoints).GetMethod("HandleGetHandleStatus", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = await (Task<IResult>)handler.Invoke(
            null, [context, handle.OperationInstanceId, store, CancellationToken.None])!;

        return ((IValueHttpResult<ApiResponse<OperationStatus>>)result).Value!.Data!;
    }
}
