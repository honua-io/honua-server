// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.SceneTools;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class McpSceneToolTests
{
    private static readonly SceneCatalogEntry PublicBuildings = new()
    {
        Id = "downtown-buildings",
        Name = "Downtown Buildings",
        Description = "3D building models",
        ServingFormat = SceneServingFormat.ThreeDTiles,
        ContentKind = SceneContentKind.Buildings,
        Extent = new SceneExtent(-157.87, 21.29, -157.84, 21.32)
    };

    private static readonly SceneCatalogEntry ProtectedCopc = new()
    {
        Id = "harbor-lidar",
        Name = "Harbor LiDAR",
        Description = "COPC airborne scan",
        ServingFormat = SceneServingFormat.Copc,
        ContentKind = SceneContentKind.PointCloud,
        TilesetFileName = "harbor.copc.laz",
        AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["lidar-reader"] }
    };

    [UnitTest]
    public async Task SceneListTool_FiltersByFormatKindAndAccess()
    {
        var reader = BuildReader([PublicBuildings, ProtectedCopc]);
        var jobService = BuildJobService();
        var tool = new SceneListTool(jobService, NullLogger<SceneListTool>.Instance);

        var basicContext = CreateHttpContext(reader, roles: ["viewer"]);
        var basicResult = await tool.InvokeAsync(basicContext, arguments: null, CancellationToken.None);
        using var basicDoc = JsonDocument.Parse(basicResult.StructuredContent!.Value.GetRawText());
        var basicJson = basicDoc.RootElement;
        basicJson.GetProperty("sceneCount").GetInt32().Should().Be(1);
        basicJson.GetProperty("scenes")[0].GetProperty("id").GetString().Should().Be("downtown-buildings");
        basicJson.GetProperty("scenes")[0].GetProperty("servingFormat").GetString().Should().Be("3d-tiles");
        basicJson.GetProperty("scenes")[0].GetProperty("contentKind").GetString().Should().Be("buildings");

        var lidarContext = CreateHttpContext(reader, roles: ["lidar-reader"]);
        using var copcArgs = JsonDocument.Parse("""{"servingFormat":"copc","contentKind":"point-cloud"}""");
        var copcResult = await tool.InvokeAsync(lidarContext, copcArgs.RootElement, CancellationToken.None);
        using var copcDoc = JsonDocument.Parse(copcResult.StructuredContent!.Value.GetRawText());
        var copcJson = copcDoc.RootElement;
        copcJson.GetProperty("sceneCount").GetInt32().Should().Be(1);
        copcJson.GetProperty("scenes")[0].GetProperty("id").GetString().Should().Be("harbor-lidar");
    }

    [UnitTest]
    public async Task SceneGetTool_ReturnsMetadataAndEnforcesAccess()
    {
        var reader = BuildReader([PublicBuildings, ProtectedCopc]);
        var tool = new SceneGetTool(BuildJobService(), NullLogger<SceneGetTool>.Instance);

        using var args = JsonDocument.Parse("""{"sceneId":"downtown-buildings"}""");
        var result = await tool.InvokeAsync(CreateHttpContext(reader, roles: ["viewer"]), args.RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.StructuredContent!.Value.GetRawText());
        var root = doc.RootElement;

        root.GetProperty("id").GetString().Should().Be("downtown-buildings");
        root.GetProperty("servingFormat").GetString().Should().Be("3d-tiles");
        root.GetProperty("contentKind").GetString().Should().Be("buildings");
        root.GetProperty("links").EnumerateArray().Should().Contain(l => l.GetProperty("rel").GetString() == "resolve");

        using var deniedArgs = JsonDocument.Parse("""{"sceneId":"harbor-lidar"}""");
        var deniedAct = () => tool.InvokeAsync(CreateHttpContext(reader, roles: ["viewer"]), deniedArgs.RootElement, CancellationToken.None);
        await deniedAct.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task SceneResolveTool_ProtectedCopcScene_EmitsPostAccessSessionWithoutCredentials()
    {
        var reader = BuildReader([PublicBuildings, ProtectedCopc]);
        var tool = new SceneResolveTool(BuildJobService(), NullLogger<SceneResolveTool>.Instance);

        using var args = JsonDocument.Parse("""{"sceneId":"harbor-lidar","preferredFormats":["copc"]}""");
        var result = await tool.InvokeAsync(CreateHttpContext(reader, roles: ["lidar-reader"]), args.RootElement, CancellationToken.None);
        var raw = result.StructuredContent!.Value.GetRawText();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        root.GetProperty("sceneId").GetString().Should().Be("harbor-lidar");
        root.GetProperty("servingFormat").GetString().Should().Be("copc");
        root.GetProperty("contentKind").GetString().Should().Be("point-cloud");

        var endpoint = root.GetProperty("endpoints").EnumerateArray().Should().ContainSingle().Subject;
        endpoint.GetProperty("endpointFormat").GetString().Should().Be("copc");
        endpoint.GetProperty("url").GetString().Should().EndWith("/scenes/harbor-lidar/copc");
        endpoint.GetProperty("requiresAuthentication").GetBoolean().Should().BeTrue();
        endpoint.GetProperty("accessSession").GetProperty("method").GetString().Should().Be("POST");
        endpoint.GetProperty("accessSession").GetProperty("href").GetString().Should().EndWith("/scenes/harbor-lidar/access-envelope");

        raw.Should().NotContain("token=");
        raw.Should().NotContain("lidar-reader");
    }

    [UnitTest]
    public void SceneTools_ClosedInputSchemas_RefuseUnknownArguments()
    {
        var tool = new SceneResolveTool(BuildJobService(), NullLogger<SceneResolveTool>.Instance);
        using var badArgs = JsonDocument.Parse("""{"sceneId":"downtown-buildings","unknownFlag":true}""");

        var act = () => McpDeclaredArgumentGuard.EnsureDeclared(tool, badArgs.RootElement);
        act.Should().Throw<GeoprocessingValidationException>().WithMessage("*unknownFlag*");
    }

    private static IGeoprocessingJobService BuildJobService()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.EnsureCallerAuthorizedAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<OperatorResourceType>(),
            Arg.Any<OperatorOperation>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return jobService;
    }

    private static ISceneCatalogReader BuildReader(SceneCatalogEntry[] entries)
    {
        var reader = Substitute.For<ISceneCatalogReader>();
        reader.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(entries);
        reader.FindActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => entries.FirstOrDefault(e => e.Id == call.Arg<string>()));
        reader.ResolveEndpoints(Arg.Any<SceneCatalogEntry>(), Arg.Any<string>())
            .Returns(call =>
            {
                var entry = call.Arg<SceneCatalogEntry>();
                var baseUrl = call.Arg<string>().TrimEnd('/');
                var format = SceneServingProjection.ToWireValue(entry.ServingFormat);
                var url = entry.ServingFormat == SceneServingFormat.Copc
                    ? $"{baseUrl}/scenes/{entry.Id}/copc"
                    : $"{baseUrl}/scenes/{entry.Id}/tileset.json";
                var accessSession = entry.RequiresAuthentication
                    ? new SceneCatalogAccessSessionAffordance($"{baseUrl}/scenes/{entry.Id}/access-envelope", "POST")
                    : null;
                return new SceneCatalogResolution
                {
                    SceneId = entry.Id,
                    ServingFormat = entry.ServingFormat,
                    ContentKind = entry.ContentKind,
                    TilesetUrl = entry.ServingFormat == SceneServingFormat.ThreeDTiles ? url : null,
                    Capabilities = [format],
                    RequiresAuthentication = entry.RequiresAuthentication,
                    AuthSchemes = entry.RequiresAuthentication ? ["Bearer", "ApiKey"] : [],
                    Endpoints =
                    [
                        new SceneCatalogEndpoint
                        {
                            Kind = format,
                            EndpointFormat = format,
                            Url = url,
                            MediaType = entry.ServingFormat == SceneServingFormat.Copc ? "application/vnd.laszip+copc" : "application/json",
                            RequiresAuthentication = entry.RequiresAuthentication,
                            AccessSession = accessSession
                        }
                    ]
                };
            });
        reader.BuildLinks(Arg.Any<SceneCatalogEntry>(), Arg.Any<string>())
            .Returns(call =>
            {
                var entry = call.Arg<SceneCatalogEntry>();
                var baseUrl = call.Arg<string>().TrimEnd('/');
                return
                [
                    new SceneCatalogLink("self", $"{baseUrl}/api/scenes/{entry.Id}", "application/json", "Scene metadata"),
                    new SceneCatalogLink("resolve", $"{baseUrl}/api/scenes/{entry.Id}/resolve", "application/json", "Scene render endpoint resolution")
                ];
            });
        return reader;
    }

    private static DefaultHttpContext CreateHttpContext(ISceneCatalogReader reader, string[] roles)
    {
        var services = new ServiceCollection();
        services.AddSingleton(reader);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddValidationServices();
        var claims = new List<Claim> { new(ClaimTypes.Name, "mcp-tester") };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("maps.example.test");
        return context;
    }
}
