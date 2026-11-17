// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ObjectStorage;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ObjectStorageConnectionDispatchTests
{
    private static readonly ObjectStorageConnection BuiltIn = new()
    {
        Id = ObjectStoragePolicy.BuiltInId(ObjectStorageBuiltInConnections.DataRole, "honua-data", "tenant/"),
        Name = "Installation data",
        Bucket = "honua-data",
        Prefix = "tenant/",
        Region = "us-east-1",
        BuiltIn = true,
    };

    [UnitTest]
    public async Task BuiltInConnections_RefuseUpdateAndDeleteWithAConflict()
    {
        var update = await InvokeAsync(ObjectStorageConnectionDispatch.UpdateAsync, new UpdateSecureConnectionRequest { Name = "Renamed" });
        var delete = await InvokeAsync(ObjectStorageConnectionDispatch.DeleteAsync, null);

        update.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        delete.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [UnitTest]
    public async Task DatabaseIds_ReachTheDatabaseHandler()
    {
        var context = Context(Guid.NewGuid(), null);
        var reached = false;

        await ObjectStorageConnectionDispatch.GetAsync(context, _ =>
        {
            reached = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        reached.Should().BeTrue();
    }

    [UnitTest]
    public void Summary_UsesEmptyDatabaseFieldsAndNoExternalId()
    {
        var summary = ObjectStorageConnectionDispatch.ToSummary(BuiltIn with { ExternalId = "never-returned" });

        summary.Host.Should().BeEmpty();
        summary.DatabaseName.Should().BeEmpty();
        summary.Kind.Should().Be(ObjectStorageConnectionKinds.Kind);
        summary.BuiltIn.Should().BeTrue();
        typeof(SecureConnectionSummary).GetProperty("ExternalId").Should().BeNull();
    }

    private static async Task<object?> InvokeAsync(
        Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> filter,
        UpdateSecureConnectionRequest? body)
        => await filter(Context(BuiltIn.Id, body), _ => throw new InvalidOperationException("A built-in must not reach the database handler."));

    private static EndpointFilterInvocationContext Context(Guid id, UpdateSecureConnectionRequest? body)
    {
        var services = new ServiceCollection()
            .AddSingleton<IObjectStorageConnectionResolver>(new SingleResolver(BuiltIn))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.RouteValues["id"] = id.ToString();
        return body is null
            ? EndpointFilterInvocationContext.Create(http, id)
            : EndpointFilterInvocationContext.Create(http, id, body);
    }

    private sealed class SingleResolver(ObjectStorageConnection connection) : IObjectStorageConnectionResolver
    {
        public Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObjectStorageConnection>>([connection]);

        public Task<ObjectStorageConnection?> FindAsync(Guid connectionId, CancellationToken cancellationToken)
            => Task.FromResult(connectionId == connection.Id ? connection : null);

        public Task<ObjectStorageResolution> ResolveObjectAsync(Guid connectionId, string? key, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
