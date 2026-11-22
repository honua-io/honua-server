// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Server.Features.Orchestration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class WorkflowBindingResolverTests
{
    private static readonly RasterSecurityContextReference SecurityContext = new()
    {
        TenantId = "tenant:test",
        AuthorizationSnapshotReference = "workflow:test:submitter"
    };

    [UnitTest]
    public void Resolve_StagedArtifactWithoutDurableIdentity_FailsBeforeSubmittingDependentStep()
    {
        var step = new WorkflowStepDefinition
        {
            StepId = "dependent",
            Plan = new AnalysisPlan
            {
                PlanId = "plan-dependent",
                IntentId = "intent-dependent",
                Steps = []
            },
            InputBindings =
            [
                new StepInputBinding
                {
                    SourceStepId = "producer",
                    SourceArtifactSelector = "artifact:0",
                    TargetInputKey = "source"
                }
            ]
        };
        var artifact = new ArtifactRef
        {
            ArtifactId = "job-1:artifact:1",
            Kind = ArtifactKind.Raster,
            Label = "output1",
            Uri = null,
            Metadata = new Dictionary<string, string>
            {
                [RasterOutputArtifactMetadata.Staged] = "true"
            }
        };
        var upstream = new Dictionary<string, WorkflowStepState>
        {
            ["producer"] = new WorkflowStepState
            {
                StepId = "producer",
                PlanId = "plan-producer",
                Status = WorkflowStepStatus.Succeeded,
                OutputArtifacts = [artifact]
            }
        };

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);

        result.ResolvedValues.Should().BeEmpty();
        result.ResolvedRasterSources.Should().BeEmpty();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("content identity is incomplete");
    }

    [UnitTest]
    public void Resolve_StagedRasterWithoutServingHostRoute_BindsTypedWorkerSource()
    {
        var step = BoundStep();
        var metadata = new Dictionary<string, string>
        {
            [RasterOutputArtifactMetadata.Staged] = "true",
            [RasterOutputArtifactMetadata.StoreProvider] = "Local",
            [RasterOutputArtifactMetadata.StoreReference] = "gp-outputs",
            [RasterOutputArtifactMetadata.ObjectKey] = "gp/outputs/job-1/a1/output/result.tif",
            [RasterOutputArtifactMetadata.SizeBytes] = "4096",
            [RasterOutputArtifactMetadata.MediaType] = "image/tiff",
            [RasterOutputArtifactMetadata.Checksum] = "sha256:" + new string('a', 64),
            [RasterOutputArtifactMetadata.GridWidth] = "32",
            [RasterOutputArtifactMetadata.GridHeight] = "16",
            [RasterOutputArtifactMetadata.GridBandCount] = "1",
            [RasterOutputArtifactMetadata.GridBitsPerSample] = "16",
            [RasterOutputArtifactMetadata.GridPixelScaleX] = "2.5",
            [RasterOutputArtifactMetadata.GridPixelScaleY] = "3.5",
        };
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:1",
            Kind = ArtifactKind.Raster,
            Label = "output1",
            Uri = null,
            ContentType = "image/tiff",
            Metadata = metadata,
        });

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);

        result.Failures.Should().BeEmpty();
        result.ResolvedValues.Should().BeEmpty();
        var staged = result.ResolvedRasterSources["source"]
            .Should().BeOfType<StagedArtifactRasterSourceDescriptor>().Subject;
        staged.ArtifactReference.Should().Be("job-1:artifact:1");
        staged.StoreReference.Should().Be("gp-outputs");
        staged.ObjectKey.Should().Be("gp/outputs/job-1/a1/output/result.tif");
        staged.Content.Checksum!.Value.Should().Be(new string('a', 64));
        staged.DeclaredDimensions.Should().Be(new RasterSourceDimensions(32, 16, 1, 16));
        staged.DeclaredPixelScale.Should().Be(new RasterSourcePixelScale(2.5, 3.5));

        var plan = new AnalysisPlan
        {
            PlanId = "plan",
            IntentId = "intent",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "native",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "raster.statistics",
                    Inputs = new Dictionary<string, string> { ["source"] = "legacy" }
                }
            ]
        };
        var bound = WorkflowBindingResolver.ApplyBindings(plan, result);
        bound.Steps[0].Inputs.Should().NotContainKey("source");
        bound.Steps[0].RasterSources["source"].Should().BeSameAs(staged);
    }

    [UnitTest]
    public void Resolve_AvailableStagedRasterWithoutBoundedGrid_FailsClosed()
    {
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:1",
            Kind = ArtifactKind.Raster,
            Label = "output1",
            Uri = "/api/geoprocessing/jobs/job-1/results/artifacts/0/content",
            ContentType = "image/tiff",
            Metadata = new Dictionary<string, string>
            {
                [RasterOutputArtifactMetadata.Staged] = "true",
                [RasterOutputArtifactMetadata.StoreProvider] = "Local",
                [RasterOutputArtifactMetadata.StoreReference] = "gp-outputs",
                [RasterOutputArtifactMetadata.ObjectKey] = "gp/outputs/job-1/a1/output/result.tif",
                [RasterOutputArtifactMetadata.SizeBytes] = "4096",
                [RasterOutputArtifactMetadata.MediaType] = "image/tiff",
                [RasterOutputArtifactMetadata.Checksum] = "sha256:" + new string('a', 64),
            },
        });

        var result = WorkflowBindingResolver.Resolve(BoundStep(), upstream, SecurityContext);

        result.ResolvedRasterSources.Should().BeEmpty();
        result.Failures.Should().ContainSingle().Which.Should().Contain("content identity is incomplete");
    }

    [UnitTest]
    public async Task Resolve_BufferArtifactDataUri_BoundToSimplifyWkb_ValidatesAndExecutesEndToEnd()
    {
        // Verbatim POINT(500000 4200000) EPSG:32610 buffer -> simplify chain from docs/guides/query-analyze/automate-workflows.md.
        const string pointWkb = "AQEAAAAAAAAAgIQeQQAAAACQBVBB";
        var catalog = new BuiltInProcessCatalog();
        var options = new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions());

        var bufferExecutor = new GeometryBufferJobExecutor(options, NullLogger<GeometryBufferJobExecutor>.Instance);
        var bufferJob = CreateGeometryJob(
            "job-buffer-1",
            "geometry.buffer",
            new Dictionary<string, string> { ["wkb"] = pointWkb, ["srid"] = "32610", ["distance"] = "500" });
        var bufferArtifactUri = await RunGeometryExecutorAsync(bufferExecutor, bufferJob);

        var simplifyStep = BuildSimplifyStep();
        var upstream = UpstreamBufferArtifact(bufferArtifactUri);

        var resolution = WorkflowBindingResolver.Resolve(simplifyStep, upstream, SecurityContext);
        resolution.Failures.Should().BeEmpty();
        resolution.ResolvedValues["wkb"].Should().NotStartWith("data:");

        var boundPlan = WorkflowBindingResolver.ApplyBindings(simplifyStep.Plan, resolution);
        var (violations, _) = ProcessPlanValidator.Validate(boundPlan, catalog);
        violations.Should().BeEmpty();

        var simplifyExecutor = new GeometrySimplifyJobExecutor(options, NullLogger<GeometrySimplifyJobExecutor>.Instance);
        var simplifyJob = CreateGeometryJob("job-simplify-1", "geometry.simplify", boundPlan.Steps[0].Inputs);
        var simplifyArtifactUri = await RunGeometryExecutorAsync(simplifyExecutor, simplifyJob);
        simplifyArtifactUri.Should().StartWith("data:application/geo+json;base64,");
    }

    private static async Task<string> RunGeometryExecutorAsync(IProcessExecutor executor, ExecutionJobRecord job)
    {
        var context = new RecordingJobExecutionContext(job.OperationId);
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        return context.PublishedArtifacts.Should().ContainSingle().Subject;
    }

    private static WorkflowStepDefinition BuildSimplifyStep() => new()
    {
        StepId = "simplify",
        Plan = new AnalysisPlan
        {
            PlanId = "plan-simplify",
            IntentId = "intent-simplify",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "simplify",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.simplify",
                    Inputs = new Dictionary<string, string> { ["wkb"] = "artifact:0", ["srid"] = "32610", ["tolerance"] = "10" }
                }
            ]
        },
        InputBindings =
        [
            new StepInputBinding { SourceStepId = "buffer", SourceArtifactSelector = "artifact:0", TargetInputKey = "wkb" }
        ]
    };

    private static Dictionary<string, WorkflowStepState> UpstreamBufferArtifact(string artifactUri) => new()
    {
        ["buffer"] = new WorkflowStepState
        {
            StepId = "buffer",
            PlanId = "plan-buffer",
            Status = WorkflowStepStatus.Succeeded,
            OutputArtifacts =
            [
                new ArtifactRef
                {
                    ArtifactId = "job-buffer-1:artifact:0",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "output0",
                    ContentType = "application/geo+json",
                    Uri = artifactUri
                }
            ]
        }
    };

    [UnitTest]
    public void Resolve_BareGeoJsonGeometryDataUri_BoundToWkbInput_ExtractsWkb()
    {
        const string pointJson = """{"type":"Point","coordinates":[12.5,34.5]}""";
        var dataUri = "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(pointJson));
        var result = WorkflowBindingResolver.Resolve(
            GeometryStep("geometry.simplify", "wkb"),
            Upstream(new ArtifactRef { ArtifactId = "job-1:artifact:0", Kind = ArtifactKind.FeatureLayer, Label = "output0", Uri = dataUri }),
            SecurityContext);

        result.Failures.Should().BeEmpty();
        var geometry = new WKBReader().Read(Convert.FromBase64String(result.ResolvedValues["wkb"]));
        geometry.Coordinate.X.Should().Be(12.5);
        geometry.Coordinate.Y.Should().Be(34.5);
    }

    [UnitTest]
    public void Resolve_GeoJson3DFeatureDataUri_BoundToWkbInput_PreservesZOrdinate()
    {
        const string featureJson = """{"type":"Feature","geometry":{"type":"Point","coordinates":[10.5,20.25,42.75]},"properties":{}}""";
        var dataUri = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(featureJson));
        var step = GeometryStep("geometry.simplify", "wkb");
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:0",
            Kind = ArtifactKind.FeatureLayer,
            Label = "output0",
            ContentType = "application/geo+json",
            Uri = dataUri
        });

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);
        result.Failures.Should().BeEmpty();
        var geometry = new WKBReader().Read(Convert.FromBase64String(result.ResolvedValues["wkb"]));
        geometry.Should().BeOfType<Point>();
        geometry.Coordinate.X.Should().Be(10.5);
        geometry.Coordinate.Y.Should().Be(20.25);
        geometry.Coordinate.Z.Should().Be(42.75);
    }

    [UnitTest]
    public void Resolve_GeoJsonFeatureCollectionDataUri_BoundToWkbArrayInput_ExtractsJsonArray()
    {
        const string fcJson = """
            {
              "type": "FeatureCollection",
              "features": [
                {"type": "Feature", "geometry": {"type": "Point", "coordinates": [1, 2]}, "properties": {}},
                {"type": "Feature", "geometry": {"type": "Point", "coordinates": [3, 4]}, "properties": {}}
              ]
            }
            """;
        var dataUri = "data:application/geo+json;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(fcJson));
        var step = GeometryStep("geometry.union", "wkbs");
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:0",
            Kind = ArtifactKind.FeatureLayer,
            Label = "output0",
            ContentType = "application/geo+json",
            Uri = dataUri
        });

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);
        result.Failures.Should().BeEmpty();
        var items = JsonSerializer.Deserialize<string[]>(result.ResolvedValues["wkbs"]);
        items.Should().NotBeNull();
        items!.Should().HaveCount(2);
        var reader = new WKBReader();
        reader.Read(Convert.FromBase64String(items[0])).Coordinate.X.Should().Be(1);
        reader.Read(Convert.FromBase64String(items[1])).Coordinate.X.Should().Be(3);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("data:text/plain;base64,aGVsbG8=", "unsupported artifact media type")]
    [InlineData("data:application/geo+json;base64,not-valid-base64!!!", "not valid base64")]
    [InlineData("data:application/geo+json;base64,eyJ0eXBlIjoiRmVhdHVyZSIsImdlb21ldHJ5IjpudWxsfQ==", "null or missing geometry")]
    [InlineData("data:application/geo+json;base64,eyJ0eXBlIjoiRmVhdHVyZUNvbGxlY3Rpb24iLCJmZWF0dXJlcyI6W119", "contains no features")]
    [InlineData("data:application/geo+json;base64,eyJ0eXBlIjoiUG9pbnQiLCJjb29yZGluYXRlcyI6WzFdfQ==", "could not be decoded")]
    public void Resolve_InvalidDataUri_BoundToWkbInput_FailsClosed(string dataUri, string expectedErrorFragment)
    {
        var step = GeometryStep("geometry.simplify", "wkb");
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:0",
            Kind = ArtifactKind.FeatureLayer,
            Label = "output0",
            Uri = dataUri
        });

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);
        result.ResolvedValues.Should().BeEmpty();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain(expectedErrorFragment);
    }

    [UnitTest]
    public void Resolve_MultiFeatureCollection_BoundToSingleWkbInput_FailsClosed()
    {
        const string fcJson = """
            {
              "type": "FeatureCollection",
              "features": [
                {"type": "Feature", "geometry": {"type": "Point", "coordinates": [1, 2]}, "properties": {}},
                {"type": "Feature", "geometry": {"type": "Point", "coordinates": [3, 4]}, "properties": {}}
              ]
            }
            """;
        var dataUri = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(fcJson));
        var step = GeometryStep("geometry.simplify", "wkb");
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:0",
            Kind = ArtifactKind.FeatureLayer,
            Label = "output0",
            Uri = dataUri
        });

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);
        result.ResolvedValues.Should().BeEmpty();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("expected a single geometry");
    }

    [UnitTest]
    public void Resolve_GeoJsonDataUri_BoundToNonWkbInput_PreservesOriginalDataUri()
    {
        const string featureJson = """{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{}}""";
        var dataUri = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(featureJson));
        var step = BoundStep();
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:0",
            Kind = ArtifactKind.FeatureLayer,
            Label = "output0",
            Uri = dataUri
        });

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);
        result.Failures.Should().BeEmpty();
        result.ResolvedValues["source"].Should().Be(dataUri);
    }

    private static WorkflowStepDefinition GeometryStep(string processId, string targetInputKey) => new()
    {
        StepId = "dependent",
        Plan = new AnalysisPlan
        {
            PlanId = "plan-dependent",
            IntentId = "intent-dependent",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "dependent",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processId,
                    Inputs = new Dictionary<string, string> { [targetInputKey] = "artifact:0" }
                }
            ]
        },
        InputBindings =
        [
            new StepInputBinding
            {
                SourceStepId = "producer",
                SourceArtifactSelector = "artifact:0",
                TargetInputKey = targetInputKey
            }
        ]
    };

    private static ExecutionJobRecord CreateGeometryJob(
        string operationId,
        string processId,
        IReadOnlyDictionary<string, string> stepInputs)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId
        };
        foreach (var (key, value) in stepInputs)
        {
            parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.{key}"] = value;
        }

        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"geoprocessing:{processId}",
                Parameters = parameters
            }
        };
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingJobExecutionContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;
        public List<string> PublishedArtifacts { get; } = [];
        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        {
            PublishedArtifacts.Add(artifactReference);
            return Task.CompletedTask;
        }
    }

    private static WorkflowStepDefinition BoundStep() => new()
    {
        StepId = "dependent",
        Plan = new AnalysisPlan { PlanId = "plan-dependent", IntentId = "intent-dependent", Steps = [] },
        InputBindings =
        [
            new StepInputBinding
            {
                SourceStepId = "producer",
                SourceArtifactSelector = "artifact:0",
                TargetInputKey = "source"
            }
        ]
    };

    private static Dictionary<string, WorkflowStepState> Upstream(ArtifactRef artifact)
        => new Dictionary<string, WorkflowStepState>
        {
            ["producer"] = new WorkflowStepState
            {
                StepId = "producer",
                PlanId = "plan-producer",
                Status = WorkflowStepStatus.Succeeded,
                OutputArtifacts = [artifact]
            }
        };
}
