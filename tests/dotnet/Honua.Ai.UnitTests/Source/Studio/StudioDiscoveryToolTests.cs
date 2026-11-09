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

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_studio_list_items")]
    public async Task ListItems_NonAdminCaller_ForcesOwnerAndTenantBeforePaging()
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.ListContentItemsAsync(Arg.Any<StudioContentItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new StudioContentItemListResult { Total = 0, NextCursor = "next-page" });
        var authorization = Substitute.For<IStudioAuthorizationService>();
        authorization.IsEndUserAuthorizationEnabled.Returns(true);
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

        var result = await ResolveTool("honua_studio_list_items").InvokeAsync(context,
            McpTestFactory.ParseJson("""{"q":"roads","cursor":"page-one","limit":10}"""), default);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("nextCursor").GetString().Should().Be("next-page");
        await lifecycle.Received(1).ListContentItemsAsync(Arg.Is<StudioContentItemQuery>(query =>
            query.OwnerId == "alice" && query.Tenant != null && query.Tenant.TenantId == "tenant-a" &&
            query.SearchTerm == "roads" && query.Cursor == "page-one" && query.Limit == 10), Arg.Any<CancellationToken>());
    }

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
