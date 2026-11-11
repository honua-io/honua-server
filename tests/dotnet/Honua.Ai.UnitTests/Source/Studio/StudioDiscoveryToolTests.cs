// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioDiscoveryToolTests
{
    [UnitTest]
    [Endpoint("POST /mcp tools/list")]
    public void PublicationVisibilitySchema_DescribesAllSupportedAudiences()
    {
        var tool = new ProposeStudioPublicationTool(Substitute.For<IGeoprocessingJobService>(),
            NullLogger<ProposeStudioPublicationTool>.Instance);
        var visibility = tool.Describe().InputSchema.GetProperty("properties").GetProperty("visibility");

        visibility.GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo("personal", "team", "organization", "public");
        visibility.GetProperty("description").GetString().Should().Contain("owner").And.Contain("team");
    }

    [UnitTheory]
    [InlineData("honua_studio_list_items")]
    [InlineData("honua_studio_list_versions")]
    [Endpoint("POST /mcp tools/list")]
    public void DiscoveryTool_Registered_AdvertisesReadOnlyContract(string name)
    {
        var tool = ResolveTool(name);
        tool.Describe().Name.Should().Be(name);
        tool.Describe().Annotations!.ReadOnlyHint.Should().BeTrue();
        tool.Describe().OutputSchema.Should().NotBeNull();
    }

    [UnitTheory]
    [InlineData(10, false)]
    [InlineData(null, false)]
    [InlineData(10, true)]
    [InlineData(null, true)]
    [Endpoint("POST /mcp tools/call honua_studio_list_items")]
    public async Task ListItems_CallerScope_AppliesOwnerAndTenantBeforePaging(int? limit, bool isAdmin)
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.ListContentItemsAsync(Arg.Any<StudioContentItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new StudioContentItemListResult { Total = 0, NextCursor = "next-page" });
        var authorization = Substitute.For<IStudioAuthorizationService>();
        authorization.IsEndUserAuthorizationEnabled.Returns(true);
        authorization.IsAdmin(Arg.Any<ClaimsPrincipal>()).Returns(isAdmin);
        authorization.ResolveCallerId(Arg.Any<ClaimsPrincipal>()).Returns("alice");
        authorization.CreateTenantScopeFilter(Arg.Any<ClaimsPrincipal>()).Returns(new TenantScopeFilter("tenant-a", false));
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<StudioAuthorizationOperation>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(StudioAuthorizationDecision.Allow());
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(authorization);
        }, user: "alice");

        var arguments = limit is { } value
            ? $$"""{"q":"roads","cursor":"page-one","limit":{{value}}}"""
            : """{"q":"roads","cursor":"page-one"}""";
        var expectedLimit = limit ?? 25;
        var result = await ResolveTool("honua_studio_list_items").InvokeAsync(context,
            McpTestFactory.ParseJson(arguments), default);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("nextCursor").GetString().Should().Be("next-page");
        await lifecycle.Received(1).ListContentItemsAsync(Arg.Is<StudioContentItemQuery>(query =>
            query.OwnerId == (isAdmin ? null : "alice") && query.Tenant != null && query.Tenant.TenantId == "tenant-a" &&
            query.SearchTerm == "roads" && query.Cursor == "page-one" && query.Limit == expectedLimit), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_studio_list_versions")]
    public async Task ListVersions_MixedOwnersAndTenants_PagesOnlyAuthorizedVersionHashes()
    {
        var itemId = Guid.NewGuid();
        StudioContentVersion Version(int number, string owner, string tenant) => new()
        {
            ItemId = itemId,
            VersionId = Guid.NewGuid(),
            VersionNumber = number,
            PackageKey = "roads",
            OwnerId = owner,
            TenantId = tenant,
            ContentHash = $"hash-{owner}-{tenant}-{number}",
            Envelope = new StudioPackageEnvelope { Family = StudioPackageFamily.Map, SchemaVersion = "1.0" },
            Validation = StudioValidationSummary.NotValidated,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        var newest = Version(4, "alice", "tenant-a");
        var foreignOwner = Version(3, "bob", "tenant-a");
        var foreignTenant = Version(2, "alice", "tenant-b");
        var oldest = Version(1, "alice", "tenant-a");
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetPointersAsync(itemId, Arg.Any<CancellationToken>()).Returns(new StudioContentItemPointers
        {
            ItemId = itemId,
            OwnerId = "alice",
            TenantId = "tenant-a",
            CurrentVersionId = newest.VersionId
        });
        lifecycle.ListVersionsAsync(itemId, Arg.Any<CancellationToken>()).Returns(new[] { oldest, foreignOwner, foreignTenant, newest });
        var authorization = OwnerAuthorization();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(authorization);
        }, user: "alice");
        var tool = ResolveTool("honua_studio_list_versions");

        var defaultPage = (await tool.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"itemId":"{{itemId}}"}"""), default)).StructuredContent!.Value;
        defaultPage.GetProperty("versions").GetArrayLength().Should().Be(2);
        defaultPage.TryGetProperty("nextBeforeVersionNumber", out _).Should().BeFalse();
        var first = (await tool.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"itemId":"{{itemId}}","limit":1}"""), default)).StructuredContent!.Value;
        first.GetProperty("versions")[0].GetProperty("contentHash").GetString().Should().Be(newest.ContentHash);
        first.GetProperty("nextBeforeVersionNumber").GetInt32().Should().Be(4);
        var second = (await tool.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"itemId":"{{itemId}}","limit":1,"beforeVersionNumber":4}"""), default)).StructuredContent!.Value;
        second.GetProperty("versions").GetArrayLength().Should().Be(1);
        second.GetProperty("versions")[0].GetProperty("versionId").GetGuid().Should().Be(oldest.VersionId);
        second.TryGetProperty("nextBeforeVersionNumber", out _).Should().BeFalse();
        second.GetRawText().Should().NotContain(foreignOwner.ContentHash).And.NotContain(foreignTenant.ContentHash);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_studio_list_items")]
    public async Task ListItems_UnresolvedCaller_DoesNotEnumerate()
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        var authorization = OwnerAuthorization();
        authorization.ResolveCallerId(Arg.Any<ClaimsPrincipal>()).Returns((string?)null);
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string?>(), Arg.Any<StudioAuthorizationOperation>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(StudioAuthorizationDecision.Deny(StudioAuthorizationService.AuthenticationRequiredCode, "Authentication is required."));
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(authorization);
        });

        var call = () => ResolveTool("honua_studio_list_items").InvokeAsync(context, McpTestFactory.ParseJson("{}"), default);

        var denied = await call.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        denied.Which.PolicyCode.Should().Be(StudioAuthorizationService.AuthenticationRequiredCode);
        await lifecycle.DidNotReceive().ListContentItemsAsync(Arg.Any<StudioContentItemQuery>(), Arg.Any<CancellationToken>());
    }

    private static IStudioAuthorizationService OwnerAuthorization()
    {
        var authorization = Substitute.For<IStudioAuthorizationService>();
        authorization.IsEndUserAuthorizationEnabled.Returns(true);
        authorization.ResolveCallerId(Arg.Any<ClaimsPrincipal>()).Returns("alice");
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string?>(), Arg.Any<StudioAuthorizationOperation>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => StudioAuthorizationDecisionFor(call.ArgAt<string?>(3), call.ArgAt<string?>(4)));
        return authorization;
    }

    private static StudioAuthorizationDecision StudioAuthorizationDecisionFor(string? owner, string? tenant) =>
        owner == "alice" && tenant is null or "tenant-a" ? StudioAuthorizationDecision.Allow() : StudioAuthorizationDecision.Deny("owner_required", "Not authorized.");

    private static IMcpTool ResolveTool(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());
        var descriptor = services.SingleOrDefault(service => service.ServiceType == typeof(IMcpTool) &&
            service.ImplementationType?.GetField("ToolName")?.GetRawConstantValue() as string == name);
        descriptor.Should().NotBeNull($"the MCP surface must register {name}");
        return (IMcpTool)ActivatorUtilities.CreateInstance(services.BuildServiceProvider(), descriptor!.ImplementationType!);
    }
}
