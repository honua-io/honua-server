// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Guards agreement between catalog-owned execution capability metadata and the
/// concrete managed dispatcher. No protocol-local process list participates in
/// the classification contract.
/// </summary>
public sealed class CatalogExecutableConformanceTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    public void Catalog_ClassifiesEveryProcessExactlyOnce()
    {
        var definitions = _catalog.ListProcesses();

        definitions.Should().HaveCount(98);
        definitions.Should().OnlyHaveUniqueItems(process => process.ProcessId);
        definitions.Should().NotContain(process => process.ExecutionKind == ProcessExecutionKind.Unclassified);
        definitions.Count(process => process.ExecutionKind == ProcessExecutionKind.Job).Should().Be(81);
        definitions.Count(process => process.ExecutionKind == ProcessExecutionKind.ProtocolOnly).Should().Be(5);
        definitions.Count(process => process.ExecutionKind == ProcessExecutionKind.WorkflowOnly).Should().Be(12);
        definitions.Count(process => process.ExecutionKind == ProcessExecutionKind.Unavailable).Should().Be(0);
    }

    [UnitTest]
    public void EveryManagedJobProcess_HasADispatcherExecutor()
    {
        var executable = DispatcherSupportedProcessIds();
        var managedJobs = _catalog.ListProcesses()
            .Where(process => process.ExecutionKind == ProcessExecutionKind.Job)
            .Where(process => RuntimeProfiles.Normalize(process.RuntimeProfile) == RuntimeProfiles.Managed);

        foreach (var process in managedJobs)
        {
            executable.Should().Contain(
                process.ProcessId,
                $"catalog classifies managed process '{process.ProcessId}' as a job, so it must have a dispatcher executor");
        }
    }

    [UnitTest]
    public void EveryManagedDispatcherExecutor_HasAJobOrWorkflowClassification()
    {
        foreach (var processId in DispatcherSupportedProcessIds())
        {
            var process = _catalog.GetProcess(processId);
            process.Should().NotBeNull($"dispatcher routes '{processId}', so the catalog must advertise it");
            new[] { ProcessExecutionKind.Job, ProcessExecutionKind.WorkflowOnly }
                .Should().Contain(
                    process!.ExecutionKind,
                    $"dispatcher route '{processId}' must be usable by either direct jobs or workflow composition");
            RuntimeProfiles.Normalize(process.RuntimeProfile).Should().Be(RuntimeProfiles.Managed);
        }
    }

    [UnitTest]
    public void ProtocolUnavailableAndNativeProcesses_AreAbsentFromTheManagedDispatcher()
    {
        var executable = DispatcherSupportedProcessIds();
        var excluded = _catalog.ListProcesses().Where(process =>
            process.ExecutionKind is ProcessExecutionKind.ProtocolOnly or ProcessExecutionKind.Unavailable
            || RuntimeProfiles.Normalize(process.RuntimeProfile) == RuntimeProfiles.Native);

        foreach (var process in excluded)
        {
            executable.Should().NotContain(
                process.ProcessId,
                $"'{process.ProcessId}' is {process.ExecutionKind} under '{process.RuntimeProfile}', not a managed dispatcher route");
        }
    }

    [UnitTest]
    public void GeometryFamily_IsFullyJobExecutable_AndMatchesTheSyncExecutionPolicy()
    {
        var executable = DispatcherSupportedProcessIds();
        var geometry = _catalog.ListProcesses().Where(process => process.Category == "geometry").ToList();

        geometry.Should().NotBeEmpty();
        foreach (var process in geometry)
        {
            process.ExecutionKind.Should().Be(ProcessExecutionKind.Job);
            process.SupportedExecutionModes.Should().HaveFlag(ProcessExecutionModes.Async);
            process.SupportedExecutionModes.Should().HaveFlag(ProcessExecutionModes.Sync);
            GPServerExecutionPolicy.IsSynchronous(process).Should().BeTrue();
            executable.Should().Contain(process.ProcessId);
        }
    }

    [UnitTest]
    public void NativeProcesses_DeclareAnExplicitCapabilityAndStayOffTheManagedDispatcher()
    {
        var managedExecutable = DispatcherSupportedProcessIds();
        var native = _catalog.ListProcesses()
            .Where(process => RuntimeProfiles.Normalize(process.RuntimeProfile) == RuntimeProfiles.Native)
            .ToList();

        native.Should().NotBeEmpty();
        native.Should().NotContain(process => process.ExecutionKind == ProcessExecutionKind.Unclassified);
        foreach (var process in native)
        {
            process.ConfigurationDependency.Should().NotBeNullOrWhiteSpace();
            managedExecutable.Should().NotContain(process.ProcessId);
        }
    }

    [UnitTest]
    public void EveryAllowedValuesParameter_DeclaresChoicesThatDifferByMoreThanCase()
    {
        foreach (var definition in _catalog.ListProcesses())
        {
            foreach (var parameter in definition.Parameters)
            {
                if (parameter.AllowedValues is not { Count: > 0 } allowed)
                {
                    continue;
                }

                allowed.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(
                    allowed.Count,
                    $"'{definition.ProcessId}' parameter '{parameter.Name}' must not declare choices differing only by case");
            }
        }
    }

    [UnitTest]
    public void EveryAllowedValuesChoice_AcceptedByGPServer_SurvivesCanonicalValidation()
    {
        foreach (var definition in _catalog.ListProcesses())
        {
            foreach (var parameter in definition.Parameters)
            {
                if (parameter.AllowedValues is not { Count: > 0 } allowed)
                {
                    continue;
                }

                foreach (var choice in allowed)
                {
                    foreach (var casing in new[] { choice, choice.ToUpperInvariant(), choice.ToLowerInvariant() })
                    {
                        var translated = GPServerParameterTranslation.TranslateInbound(
                            new Dictionary<string, string>(StringComparer.Ordinal) { [parameter.Name] = casing },
                            definition);

                        translated[parameter.Name].Should().Be(choice);
                        var plan = SingleStepPlan(definition.ProcessId, translated);
                        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);
                        violations.Should().NotContain(
                            violation => violation.FieldPath == $"steps[s1].inputs.{parameter.Name}");
                    }
                }
            }
        }
    }

    private static AnalysisPlan SingleStepPlan(string processId, IReadOnlyDictionary<string, string> inputs) =>
        new()
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processId,
                    Inputs = new Dictionary<string, string>(inputs, StringComparer.Ordinal)
                }
            ]
        };

    private IReadOnlyCollection<string> DispatcherSupportedProcessIds()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        IProcessExecutor[] executors =
        [
            new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance),
            new GeometryClipJobExecutor(monitor, NullLogger<GeometryClipJobExecutor>.Instance),
            new GeometryIntersectJobExecutor(monitor, NullLogger<GeometryIntersectJobExecutor>.Instance),
            new GeometryProjectJobExecutor(monitor, NullLogger<GeometryProjectJobExecutor>.Instance),
            new GeometryAreaJobExecutor(monitor, NullLogger<GeometryAreaJobExecutor>.Instance),
            new GeometryUnionJobExecutor(monitor, NullLogger<GeometryUnionJobExecutor>.Instance),
            new GeometryCentroidJobExecutor(monitor, NullLogger<GeometryCentroidJobExecutor>.Instance),
            new GeometryLengthJobExecutor(monitor, NullLogger<GeometryLengthJobExecutor>.Instance),
            new GeometryConvexHullJobExecutor(monitor, NullLogger<GeometryConvexHullJobExecutor>.Instance),
            new GeometryDissolveJobExecutor(monitor, NullLogger<GeometryDissolveJobExecutor>.Instance),
            new GeometrySimplifyJobExecutor(monitor, NullLogger<GeometrySimplifyJobExecutor>.Instance),
            new GeometrySnapJobExecutor(monitor, NullLogger<GeometrySnapJobExecutor>.Instance),
            new GeometryMakeValidJobExecutor(monitor, NullLogger<GeometryMakeValidJobExecutor>.Instance),
            new GeometryDifferenceJobExecutor(monitor, NullLogger<GeometryDifferenceJobExecutor>.Instance),
            new GeometryFormatJobExecutor(monitor),
            new ManagedSpatialJoinExecutor(monitor),
            new ManagedClusterExecutor(monitor),
            new ManagedBufferAggregateExecutor(monitor),
            new ManagedDensityExecutor(monitor),
            new ManagedHotSpotExecutor(monitor),
            new LayerBufferAggregateExecutor(scopeFactory, monitor, NullLogger<LayerBufferAggregateExecutor>.Instance),
            new LayerFeatureProjectExecutor(scopeFactory, monitor, NullLogger<LayerFeatureProjectExecutor>.Instance),
            new LayerDissolveExecutor(scopeFactory, monitor, NullLogger<LayerDissolveExecutor>.Instance),
            new LayerSimplifyExecutor(scopeFactory, monitor, NullLogger<LayerSimplifyExecutor>.Instance),
            new LayerSpatialJoinExecutor(scopeFactory, monitor, NullLogger<LayerSpatialJoinExecutor>.Instance),
            new EnrichmentJobExecutor(scopeFactory, monitor, NullLogger<EnrichmentJobExecutor>.Instance),
            new OverlayClipExecutor(monitor),
            new OverlayIntersectExecutor(monitor),
            new OverlayUnionExecutor(monitor),
            new OverlayEraseExecutor(monitor),
            new OverlayMergeExecutor(monitor),
            new OverlaySplitExecutor(monitor),
            new DataManagementAppendExecutor(monitor),
            new ProximityNearExecutor(monitor),
            new ProximityNearTableExecutor(monitor),
            new StatisticsSummarizeExecutor(monitor),
            new StatisticsFrequencyExecutor(monitor),
            new StatisticsCalculateExecutor(monitor),
            new AttributeRenameTransformExecutor(monitor),
            new AttributeCastTransformExecutor(monitor),
            new ComputedFieldTransformExecutor(monitor),
            new AttributeFilterTransformExecutor(monitor),
            new AttributeJoinTransformExecutor(monitor),
            new AggregateTransformExecutor(monitor),
            new PivotTransformExecutor(monitor),
            new UnpivotTransformExecutor(monitor),
            new SpatialFilterTransformExecutor(monitor),
            new ClipTransformExecutor(monitor),
            new DedupTransformExecutor(monitor),
            new ReprojectTransformExecutor(monitor),
            new GeoJsonSourceExecutor(monitor),
            new CsvSourceExecutor(monitor),
            new GeoJsonFileSinkExecutor(monitor),
            new QuarantineSinkExecutor(monitor),
            new ExternalPostgisSinkExecutor(monitor),
            new HonuaLayerSinkExecutor(monitor, NullLogger<HonuaLayerSinkExecutor>.Instance),
            new ImportDatasetJobExecutor(
                Substitute.For<IServiceScopeFactory>(),
                NullLogger<ImportDatasetJobExecutor>.Instance,
                Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>()),
            new ImageryInferenceJobExecutor(
                Substitute.For<IOptionsMonitor<Honua.Geoprocessing.Inference.ImageryInferenceOptions>>(),
                monitor,
                [],
                NullLogger<ImageryInferenceJobExecutor>.Instance),
        ];

        var allExecutors = executors.Concat(BuildRemoteSourceExecutors(monitor)).ToArray();
        var dispatcher = new GeoprocessingDispatchJobExecutor(
            allExecutors,
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance);

        return dispatcher.SupportedProcessIds;
    }

    private static RemoteSourceExecutor[] BuildRemoteSourceExecutors(
        IOptionsMonitor<GeoprocessingExecutorOptions> monitor)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        string[] sourceIds =
        [
            "source.honua-layer",
            "source.esri-featureserver",
            "source.ogc-features",
            "source.wfs",
            "source.postgis",
        ];

        return sourceIds
            .Select(id => RemoteSourceExecutor.ForProcess(
                id,
                scopeFactory,
                monitor,
                NullLogger<RemoteSourceExecutor>.Instance))
            .ToArray();
    }
}
