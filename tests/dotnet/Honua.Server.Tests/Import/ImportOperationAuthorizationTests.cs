// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Import.FileImport;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[SecurityTest]
public sealed class ImportOperationAuthorizationTests
{
    [UnitTheory]
    [InlineData(false, false, "tenant-a", "tenant-a", false, true)]
    [InlineData(false, false, "tenant-a", "tenant-b", false, false)]
    [InlineData(false, true, "tenant-a", "tenant-b", false, true)]
    [InlineData(false, false, "tenant-a", null, false, false)]
    [InlineData(false, false, null, null, false, true)]
    [InlineData(false, true, "tenant-a", null, false, true)]
    [InlineData(true, false, "tenant-a", "tenant-a", true, true)]
    [InlineData(true, false, "tenant-a", "tenant-a", false, false)]
    [InlineData(true, true, "tenant-a", "tenant-b", true, false)]
    [InlineData(true, true, "tenant-a", "tenant-a", false, false)]
    [InlineData(true, false, "tenant-a", null, true, false)]
    [InlineData(true, false, null, null, true, false)]
    public async Task Access_EnforcesPersistedOwnershipAndTenant(
        bool agent, bool platformAdmin, string? currentTenant, string? storedTenant, bool sameActor, bool allowed)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(currentTenant);
        using var services = new ServiceCollection().AddSingleton(authorization).AddSingleton(tenant).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test");
        if (agent) identity.AddClaim(new Claim("permission", "agent:workspace"));
        if (platformAdmin) identity.AddClaim(new Claim(ClaimTypes.Role, "platform_admin"));
        var context = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(identity) };
        // Caller-supplied workspace headers cannot grant access to another actor's job.
        context.Request.Headers["X-Honua-Workspace"] = "other-workspace";
        context.Request.Headers["X-Honua-Turn"] = "other-turn";
        var owner = sameActor ? CanonicalSecurityActor.Resolve(context.User)!.ActorId : "another-actor";
        var ownership = new ImportOperationOwnership(owner, storedTenant);

        (await ImportOperationAuthorization.CanAccessAsync(context, ownership, default)).Should().Be(allowed);
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        (await ImportOperationAuthorization.CanAccessAsync(context, ownership, default)).Should().BeFalse();
    }

    [UnitTest]
    public async Task LegacyOwnership_IsNeverInferredForAnAgent()
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        using var services = new ServiceCollection().AddSingleton(authorization).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "agent"), new Claim("permission", "agent:workspace")], "test")),
        };
        (await ImportOperationAuthorization.CanAccessAsync(context, null, default)).Should().BeFalse();
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        ImportOperationAuthorization.Capture(context).Should().BeNull();
        (await ImportOperationAuthorization.CanAccessAsync(context, null, default)).Should().BeFalse();
    }
}
