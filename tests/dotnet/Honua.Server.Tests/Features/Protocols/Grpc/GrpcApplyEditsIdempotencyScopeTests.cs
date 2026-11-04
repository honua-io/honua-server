// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Durable gRPC applyEdits receipts are stored under the scope hash, so the hash for a given
/// caller, tenant, layer and key must never change: a different hash would re-execute a
/// committed edit on retry.
/// </summary>
public sealed class GrpcApplyEditsIdempotencyScopeTests
{
    [UnitTheory]
    [InlineData("tenant-a", "f2ace86bd6241cff881a6baca05866bf3f46547ba2c20fa8f095250749502d73")]
    [InlineData(null, "dd8d822765166ea990e3908f4bbce633f3556bef061dc6a5ee6b23311c05e089")]
    public void CreateScope_IssuerQualifiedSubject_KeepsItsHash(string? tenantId, string expectedScope)
    {
        var editor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "editor"), new Claim("iss", "https://issuer-a.example")], "Bearer"));

        GrpcApplyEditsIdempotencyStore.CreateScope(Request(editor, tenantId), "svc", 3, "client-key")
            .Should().Be(expectedScope);
    }

    [UnitTest]
    public void CreateScope_Anonymous_KeepsItsHash()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        GrpcApplyEditsIdempotencyStore.CreateScope(Request(anonymous, "tenant-a"), "svc", 3, "client-key")
            .Should().Be("f85b3f4d706b64671c45adbae398014ad103501e5466ded0358a95adeb302e1c");
    }

    private static DefaultHttpContext Request(ClaimsPrincipal user, string? tenantId)
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenantId);
        var services = new ServiceCollection().AddSingleton(tenantContext);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = user };
    }
}
