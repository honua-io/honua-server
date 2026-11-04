// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Security;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// A REST applyEdits response stored under an Idempotency-Key replays only to the same scheme- and
/// issuer-qualified actor, matching the gRPC ApplyEdits scope.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
public sealed class ApplyEditsIdempotencyCallerScopeTests
{
    private const string IssuerA = "https://issuer-a.example";
    private const string IssuerB = "https://issuer-b.example";

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task TryGetAsync_SameIssuerQualifiedSubject_Replays()
    {
        var store = NewStore();
        await RecordAsync(store, Caller(Subject("editor", IssuerA)));

        var replay = await store.TryGetAsync(ScopeFor(Caller(Subject("editor", IssuerA))));

        replay.Should().NotBeNull("a retry by the caller that made the edit must replay it");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task TryGetAsync_SameSubjectFromAnotherIssuer_DoesNotReplay()
    {
        var store = NewStore();
        await RecordAsync(store, Caller(Subject("shared-sub", IssuerA)));

        var replay = await store.TryGetAsync(ScopeFor(Caller(Subject("shared-sub", IssuerB))));

        replay.Should().BeNull("a subject from another issuer is a different actor");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task TryGetAsync_SameSubjectFromAnotherAuthenticationScheme_DoesNotReplay()
    {
        var store = NewStore();
        await RecordAsync(store, Caller(Subject("shared-sub", issuer: null, scheme: "SchemeA")));

        var replay = await store.TryGetAsync(ScopeFor(Caller(Subject("shared-sub", issuer: null, scheme: "SchemeB"))));

        replay.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void Resolve_AuthenticatedCallerWithoutBindableIdentity_ReturnsNull()
    {
        var roleOnly = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Bearer"));

        IdempotencyCaller.Resolve(new DefaultHttpContext { User = roleOnly }).Should().BeNull(
            "a keyed edit is refused when its retries cannot be bound to a durable actor");
    }

    private static DistributedApplyEditsIdempotencyStore NewStore()
        => new(cache: null, NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

    private static async Task RecordAsync(DistributedApplyEditsIdempotencyStore store, IdempotencyCaller caller)
    {
        var response = new ApplyEditsResponse { Success = true, AddResults = [new EditResult { ObjectId = 7, Success = true }] };
        (await store.SetAsync(ScopeFor(caller), "unreserved", response)).Should().BeTrue();
    }

    private static ApplyEditsIdempotencyScope ScopeFor(IdempotencyCaller caller) => new("svc", 0, caller, "client-key");

    private static IdempotencyCaller Caller(ClaimsPrincipal user)
        => IdempotencyCaller.Resolve(new DefaultHttpContext { User = user })
           ?? throw new InvalidOperationException("The test caller must be bindable.");

    private static ClaimsPrincipal Subject(string subject, string? issuer, string scheme = "Bearer")
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (issuer is not null)
        {
            claims.Add(new Claim("iss", issuer));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }
}
