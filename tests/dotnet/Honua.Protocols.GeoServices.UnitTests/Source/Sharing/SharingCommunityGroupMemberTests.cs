// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices.Sharing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Sharing;

/// <summary>
/// A public group's member roster is visible only to its owner, its members, or an administrator.
/// </summary>
public sealed class SharingCommunityGroupMemberTests
{
    [UnitTest]
    public async Task GetGroup_AnonymousPublicGroup_OmitsMembers()
    {
        using var json = await ReadGroupAsync(principal: new ClaimsPrincipal(new ClaimsIdentity()));

        json.RootElement.TryGetProperty("members", out _).Should().BeFalse();
        json.RootElement.GetProperty("title").GetString().Should().Be("Public group");
        json.RootElement.GetProperty("owner").GetString().Should().Be("alice");
    }

    [UnitTest]
    public async Task GetGroup_AuthenticatedNonMember_OmitsMembers()
    {
        var principal = Principal("mallory");

        using var json = await ReadGroupAsync(principal);

        json.RootElement.TryGetProperty("members", out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task GetGroup_OwnerMemberAndAdmin_IncludeMembers()
    {
        foreach (var principal in new[] { Principal("alice"), Principal("bob"), Admin("operator") })
        {
            using var json = await ReadGroupAsync(principal);
            json.RootElement.GetProperty("members").EnumerateArray()
                .Select(member => member.GetString())
                .Should().Equal("alice", "bob");
        }
    }

    private static async Task<JsonDocument> ReadGroupAsync(ClaimsPrincipal principal)
    {
        var store = Substitute.For<IPortalGroupStore>();
        store.GetAsync("group-1", Arg.Any<CancellationToken>()).Returns(Group());
        var context = CreateContext(principal);
        var result = await SharingCommunityEndpoints.HandleGetGroupAsync(
            context,
            "group-1",
            store,
            NullLogger<SharingRestLog>.Instance);

        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static DefaultHttpContext CreateContext(ClaimsPrincipal principal)
    {
        var license = Substitute.For<ILicenseEntitlementService>();
        license.CheckEntitlement(Arg.Any<string>()).Returns(call => new LicenseEntitlementDecision(
            call.Arg<string>(),
            true,
            HonuaEdition.Community,
            LicenseValidationState.Valid,
            null,
            string.Empty));
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(license);
        services.AddSingleton(tenant);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = principal,
            Response = { Body = new MemoryStream() },
        };
    }

    private static PortalGroupRecord Group() => new(
        Id: "group-1",
        Title: "Public group",
        Description: null,
        Owner: "alice",
        Access: PortalGroupAccess.Public,
        Tags: [],
        Members: ["alice", "bob"],
        Created: DateTimeOffset.UnixEpoch,
        Modified: DateTimeOffset.UnixEpoch,
        TenantId: "tenant-a");

    private static ClaimsPrincipal Principal(string username)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, username)], authenticationType: "Test"));

    private static ClaimsPrincipal Admin(string username)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, username), new Claim(ClaimTypes.Role, "admin")],
            authenticationType: "Test"));
}
