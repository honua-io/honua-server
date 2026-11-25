// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ObjectStorage;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    [UnitTest]
    public void ApplyUpdate_DescriptionOverCreateLimit_IsRefused()
    {
        var customer = BuiltIn with { BuiltIn = false };

        var (tooLong, error) = ObjectStorageConnectionDispatch.ApplyUpdate(
            customer, new UpdateSecureConnectionRequest { Description = new string('d', 1001) });
        var (atLimit, _) = ObjectStorageConnectionDispatch.ApplyUpdate(
            customer, new UpdateSecureConnectionRequest { Description = new string('d', 1000) });

        tooLong.Should().BeNull("update must keep the 1000-character limit create enforces");
        error.Should().Be("description must be at most 1000 characters.");
        atLimit!.Description.Should().HaveLength(1000);
    }

    [UnitTest]
    public async Task TenantBoundAdmin_CannotCreateOrReadGlobalObjectStorageConnections()
    {
        var createBody = new CreateSecureConnectionRequest { Name = "tenant-bucket", Kind = ObjectStorageConnectionKinds.Kind };
        var createContext = TenantAdminContext(BuiltIn.Id, createBody);

        var createResult = await ObjectStorageConnectionDispatch.CreateAsync(
            createContext,
            _ => throw new InvalidOperationException("Object-storage create must not fall through to the DB handler."));
        var testDraftResult = await ObjectStorageConnectionDispatch.TestDraftAsync(
            createContext,
            _ => throw new InvalidOperationException("Object-storage draft test must not fall through to the DB handler."));

        createResult.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        testDraftResult.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var dbFallbackReached = false;
        var getContext = TenantAdminContext(BuiltIn.Id, null);
        await ObjectStorageConnectionDispatch.GetAsync(getContext, _ =>
        {
            dbFallbackReached = true;
            return ValueTask.FromResult<object?>(Results.NotFound());
        });
        dbFallbackReached.Should().BeTrue("tenant-bound callers must not resolve global object-storage connections");
    }

    [UnitTest]
    public void SetupPrincipal_QualifiesSubjectByIssuerAndTenant()
    {
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new TenantContextOptions { Enabled = true }))
            .BuildServiceProvider();

        var firstIssuer = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("iss", "https://idp-a.example"), new Claim("tid", "tenant-a")],
                "Bearer")),
        };
        var secondIssuer = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("iss", "https://idp-b.example"), new Claim("tid", "tenant-a")],
                "Bearer")),
        };
        var secondTenant = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("iss", "https://idp-a.example"), new Claim("tid", "tenant-b")],
                "Bearer")),
        };

        var key1 = ObjectStorageConnectionEndpoints.SetupPrincipal(firstIssuer);
        var key2 = ObjectStorageConnectionEndpoints.SetupPrincipal(secondIssuer);
        var key3 = ObjectStorageConnectionEndpoints.SetupPrincipal(secondTenant);

        key1.Should().NotBe(key2);
        key1.Should().NotBe(key3);
        key2.Should().NotBe(key3);
    }

    private static EndpointFilterInvocationContext TenantAdminContext(Guid id, object? body)
    {
        var services = new ServiceCollection()
            .AddSingleton<IObjectStorageConnectionResolver>(new SingleResolver(BuiltIn))
            .AddSingleton(Options.Create(new TenantContextOptions { Enabled = true }))
            .BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "tenant-admin"), new Claim(ClaimTypes.Role, "admin"), new Claim("tid", "tenant-a")],
                "Bearer")),
        };
        http.Request.RouteValues["id"] = id.ToString();
        return body is null
            ? EndpointFilterInvocationContext.Create(http, id)
            : EndpointFilterInvocationContext.Create(http, body);
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
