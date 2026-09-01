// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class McpAnalysisProfileTests
{
    [UnitTest]
    public void CapabilityManifest_IsProfileAware()
    {
        var baseManifest = Honua.Ai.Protocols.Mcp.Discovery.CapabilityManifestEmitter.EmitManifest();
        var analysisManifest = Honua.Ai.Protocols.Mcp.Discovery.CapabilityManifestEmitter.EmitManifest(["analysis"]);

        baseManifest.Profiles.Should().Equal("base");
        baseManifest.Tools.Should().NotContain(tool => tool.AdvertisedName == BufferFeaturesTool.ToolName);
        analysisManifest.Profiles.Should().BeEquivalentTo(["base", "analysis"]);
        analysisManifest.Tools.Should().Contain(tool => tool.AdvertisedName == BufferFeaturesTool.ToolName);
        analysisManifest.Tools.Count.Should().Be(baseManifest.Tools.Count + 6);
    }

    [UnitTest]
    public void AnalysisTools_AreHiddenByDefault_AndAdvertisedWhenEnabled()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        IMcpTool[] tools =
        [
            new BufferFeaturesTool(jobService, NullLogger<BufferFeaturesTool>.Instance),
            new OverlayFeaturesTool(jobService, NullLogger<OverlayFeaturesTool>.Instance),
            new SummarizeStatisticsTool(jobService, NullLogger<SummarizeStatisticsTool>.Instance),
            new ReprojectFeaturesTool(jobService, NullLogger<ReprojectFeaturesTool>.Instance),
            new JoinFeaturesTool(jobService, NullLogger<JoinFeaturesTool>.Instance),
            new ExportDatasetTool(jobService, NullLogger<ExportDatasetTool>.Instance)
        ];

        var baseSurface = new McpDataAccessSurface(
            tools,
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            options: Options.Create(new McpOptions()));
        var analysisSurface = new McpDataAccessSurface(
            tools,
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            options: Options.Create(new McpOptions { Profiles = ["base", "analysis"] }));

        baseSurface.ToolNames.Should().BeEmpty();
        analysisSurface.ToolNames.Should().BeEquivalentTo(tools.Select(tool => tool.Name));
    }

    [UnitTest]
    public async Task Initialize_AdvertisesEnabledAnalysisProfile()
    {
        var surface = new McpDataAccessSurface(
            [],
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            options: Options.Create(new McpOptions { Profiles = ["analysis"] }));
        using var paramsDocument = JsonDocument.Parse(
            """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"analysis-test","version":"1"}}""");
        using var idDocument = JsonDocument.Parse("1");

        var response = await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            new McpJsonRpcRequest
            {
                JsonRpc = "2.0",
                Id = idDocument.RootElement.Clone(),
                Method = "initialize",
                Params = paramsDocument.RootElement.Clone()
            },
            CancellationToken.None);

        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("capabilities").GetProperty("profiles")
            .EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo(["base", "analysis"]);
    }

    [UnitTest]
    public async Task Initialize_DoesNotAdvertiseUnknownConfiguredProfile()
    {
        var surface = new McpDataAccessSurface(
            [],
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            options: Options.Create(new McpOptions { Profiles = ["analysys"] }));
        using var paramsDocument = JsonDocument.Parse(
            """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"analysis-test","version":"1"}}""");
        using var idDocument = JsonDocument.Parse("1");

        var response = await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            new McpJsonRpcRequest
            {
                JsonRpc = "2.0",
                Id = idDocument.RootElement.Clone(),
                Method = "initialize",
                Params = paramsDocument.RootElement.Clone()
            },
            CancellationToken.None);

        response!.Result!.Value.GetProperty("capabilities").GetProperty("profiles")
            .EnumerateArray().Select(value => value.GetString())
            .Should().Equal("base");
    }

    [UnitTest]
    public async Task BufferFeatures_SubmitsSingleCanonicalJobPlan()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        AnalysisPlan? submitted = null;
        jobService.SubmitJobAsync(
                Arg.Do<AnalysisPlan>(plan => submitted = plan),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob());
        var tool = new BufferFeaturesTool(jobService, NullLogger<BufferFeaturesTool>.Instance);
        using var arguments = JsonDocument.Parse(
            """{"source":{"serviceId":"county_roads","layerId":0},"distance":500,"unit":"meters","dissolve":true,"where":"road_class = 'arterial'","outSrid":3857}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            arguments.RootElement,
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        submitted.Should().NotBeNull();
        submitted!.Steps.Should().ContainSingle();
        submitted.Steps[0].ProcessId.Should().Be("analytics.buffer-aggregate");
        submitted.Steps[0].Inputs.Should().ContainKey("layerId").WhoseValue.Should().Be("0");
        submitted.Steps[0].Inputs.Should().ContainKey("distance").WhoseValue.Should().Be("500");
    }

    [UnitTest]
    public async Task BufferFeatures_ProjectsFalseDissolveDefault()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        AnalysisPlan? submitted = null;
        jobService.SubmitJobAsync(
                Arg.Do<AnalysisPlan>(plan => submitted = plan),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob());
        var tool = new BufferFeaturesTool(jobService, NullLogger<BufferFeaturesTool>.Instance);
        using var arguments = JsonDocument.Parse(
            """{"source":{"serviceId":"county_roads","layerId":0},"distance":500,"unit":"meters"}""");

        await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments.RootElement, CancellationToken.None);

        submitted!.Steps[0].Inputs.Should().ContainKey("dissolve").WhoseValue.Should().Be("false");
    }

    [UnitTest]
    public async Task AnalysisVerb_IdempotentTerminalReplay_ReturnsArtifactReferences()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.SubmitJobAsync(
                Arg.Any<AnalysisPlan>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob() with { Status = ExecutionJobStatus.Succeeded });
        jobService.GetJobResultsAsync(
                "analysis-job-1",
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(AnalysisResultPackage.CreateCompleted(
                resultPackageId: "analysis-result-1",
                summary: new ResultSummary { Title = "Buffered roads" },
                artifacts:
                [
                    new ArtifactRef
                    {
                        ArtifactId = "artifact-buffer-1",
                        Kind = ArtifactKind.FeatureLayer,
                        Label = "Buffered roads",
                        Uri = "honua://analysis/artifacts/artifact-buffer-1",
                        ContentType = "application/geo+json"
                    }
                ],
                workspaceRefs: [],
                provenance: new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }));
        var tool = new BufferFeaturesTool(jobService, NullLogger<BufferFeaturesTool>.Instance);
        using var arguments = JsonDocument.Parse(
            """{"source":{"serviceId":"county_roads","layerId":0},"distance":500,"unit":"meters"}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments.RootElement, CancellationToken.None);

        var output = result.StructuredContent!.Value;
        output.GetProperty("status").GetString().Should().Be("succeeded");
        var artifact = output.GetProperty("artifacts").EnumerateArray().Should().ContainSingle().Subject;
        artifact.GetProperty("artifactId").GetString().Should().Be("artifact-buffer-1");
        artifact.GetProperty("uri").GetString().Should().Be("honua://analysis/artifacts/artifact-buffer-1");
    }

    [Theory]
    [InlineData("buffer", "buffer_features/buffer_features.json")]
    [InlineData("overlay", "overlay_features/overlay_features.json")]
    [InlineData("summarize", "summarize_statistics/summarize_statistics.json")]
    [InlineData("reproject", "reproject_features/reproject_features.json")]
    [InlineData("join", "join_features/join_features_attribute.json")]
    [InlineData("join", "join_features/join_features_spatial.json")]
    [InlineData("export", "export_dataset/export_dataset.json")]
    public async Task GeospatialMcpFixture_RoundTripsToTerminalJobAndArtifact(
        string verb,
        string fixtureRelativePath)
    {
        var fixturePath = Path.Join(
            AppContext.BaseDirectory,
            "ConformanceSchemas",
            "geospatial-mcp",
            "conformance",
            "fixtures",
            "tools",
            fixtureRelativePath);
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var inputs = fixture.RootElement.GetProperty("inputs");
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.SubmitJobAsync(
                Arg.Any<AnalysisPlan>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob() with { Status = ExecutionJobStatus.Succeeded });
        jobService.GetJobResultsAsync(
                "analysis-job-1",
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(AnalysisResultPackage.CreateCompleted(
                "analysis-result-1",
                new ResultSummary { Title = fixture.RootElement.GetProperty("id").GetString()! },
                [new ArtifactRef
                {
                    ArtifactId = "artifact-fixture-1",
                    Kind = verb == "summarize" ? ArtifactKind.Table : ArtifactKind.FeatureLayer,
                    Label = "Fixture result",
                    Uri = "honua://analysis/artifacts/artifact-fixture-1",
                    ContentType = "application/geo+json"
                }],
                [],
                new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }));

        var result = await CreateAnalysisTool(verb, jobService).InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), inputs, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent.Should().NotBeNull();
        var structuredContent = result.StructuredContent!.Value;
        structuredContent.GetProperty("status").GetString().Should().Be("succeeded");
        structuredContent.GetProperty("artifacts").GetArrayLength().Should().Be(1);
    }

    [UnitTest]
    public async Task ExportDataset_DoesNotEnableDeferredGeoPackageSink()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var tool = new ExportDatasetTool(jobService, NullLogger<ExportDatasetTool>.Instance);
        using var arguments = JsonDocument.Parse(
            """{"source":{"serviceId":"roads","layerId":0},"format":"geopackage"}""");

        var action = () => tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments.RootElement, CancellationToken.None);

        await action.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*use 'geojson'*");
        await jobService.DidNotReceiveWithAnyArgs().SubmitJobAsync(
            default!, default, default!, default, default);
    }

    [Theory]
    [InlineData("buffer", "{\"source\":{\"serviceId\":\"roads\",\"layerId\":0},\"distance\":500,\"unit\":\"meters\",\"dissolve\":true}")]
    [InlineData("buffer", "{\"source\":{\"artifactId\":\"artifact-roads\"},\"distance\":25,\"unit\":\"meters\"}")]
    [InlineData("overlay", "{\"source\":{\"serviceId\":\"parcels\",\"layerId\":0},\"overlay\":{\"artifactId\":\"artifact-flood\"},\"operation\":\"intersect\"}")]
    [InlineData("summarize", "{\"source\":{\"serviceId\":\"tracts\",\"layerId\":0},\"groupByFields\":[\"county\"],\"statistics\":[{\"statisticType\":\"sum\",\"onField\":\"population\"}]}")]
    [InlineData("reproject", "{\"source\":{\"serviceId\":\"roads\",\"layerId\":0},\"targetSrid\":3857}")]
    [InlineData("reproject", "{\"source\":{\"artifactId\":\"artifact-roads\"},\"sourceSrid\":4326,\"targetSrid\":3857}")]
    [InlineData("join", "{\"target\":{\"serviceId\":\"incidents\",\"layerId\":0},\"join\":{\"serviceId\":\"counties\",\"layerId\":1},\"joinType\":\"spatial\",\"spatialRelationship\":\"within\"}")]
    [InlineData("join", "{\"target\":{\"artifactId\":\"artifact-incidents\"},\"join\":{\"artifactId\":\"artifact-counties\"},\"joinType\":\"spatial\",\"spatialRelationship\":\"intersects\"}")]
    [InlineData("join", "{\"target\":{\"serviceId\":\"tracts\",\"layerId\":0},\"join\":{\"serviceId\":\"demographics\",\"layerId\":1},\"joinType\":\"attribute\",\"targetField\":\"geoid\",\"joinField\":\"tract_geoid\"}")]
    [InlineData("export", "{\"source\":{\"serviceId\":\"roads\",\"layerId\":0},\"format\":\"geojson\"}")]
    public async Task AnalysisVerbPlans_ValidateAgainstBuiltInCatalog(
        string verb,
        string argumentJson)
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        AnalysisPlan? submitted = null;
        jobService.SubmitJobAsync(
                Arg.Do<AnalysisPlan>(plan => submitted = plan),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob());
        var tool = CreateAnalysisTool(verb, jobService);
        using var arguments = JsonDocument.Parse(argumentJson);

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            arguments.RootElement,
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        submitted.Should().NotBeNull();
        var graphValidation = () => AnalysisPlanGraphValidator.Validate(submitted!);
        graphValidation.Should().NotThrow();
        var (violations, _) = ProcessPlanValidator.Validate(submitted!, new BuiltInProcessCatalog());
        violations.Should().BeEmpty(
            "every direct analysis verb variant must produce a plan accepted by the live catalog validator");
    }

    private static IMcpTool CreateAnalysisTool(string verb, IGeoprocessingJobService jobService)
        => verb switch
        {
            "buffer" => new BufferFeaturesTool(jobService, NullLogger<BufferFeaturesTool>.Instance),
            "overlay" => new OverlayFeaturesTool(jobService, NullLogger<OverlayFeaturesTool>.Instance),
            "summarize" => new SummarizeStatisticsTool(jobService, NullLogger<SummarizeStatisticsTool>.Instance),
            "reproject" => new ReprojectFeaturesTool(jobService, NullLogger<ReprojectFeaturesTool>.Instance),
            "join" => new JoinFeaturesTool(jobService, NullLogger<JoinFeaturesTool>.Instance),
            "export" => new ExportDatasetTool(jobService, NullLogger<ExportDatasetTool>.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null)
        };

    private static ExecutionJobRecord CreateQueuedJob()
        => new()
        {
            OperationId = "analysis-job-1",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "analysis",
                Parameters = new Dictionary<string, string>()
            }
        };
}
