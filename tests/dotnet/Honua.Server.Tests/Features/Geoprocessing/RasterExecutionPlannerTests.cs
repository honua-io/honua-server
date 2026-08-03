// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class RasterExecutionPlannerTests
{
    private readonly RasterExecutionPlanner _sut = new(
        CreateRegistry(),
        NullLogger<RasterExecutionPlanner>.Instance);

    [Fact]
    public void Plan_DataResidentBoundedWork_SelectsDurablePostgis()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 32 * MiB, scratchBytes: 64 * MiB, databaseWork: 2_000_000)));

        decision.Engine.Should().Be(RasterEngine.Postgis);
        decision.Placement.Should().Be(RasterExecutionPlacement.DurablePostgis);
        decision.ReasonCode.Should().Be("postgis-source-local");
        decision.PolicyRef.Should().Be("test-policy");
    }

    [Fact]
    public void Plan_RequestEligibleDataResidentWork_SelectsBoundedPostgisRequest()
    {
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            AllowRequestExecution = true,
        };

        var decision = _sut.Plan(request);

        decision.Engine.Should().Be(RasterEngine.Postgis);
        decision.Placement.Should().Be(RasterExecutionPlacement.Request);
        decision.ReasonCode.Should().Be("postgis-request-budget");
    }

    [Fact]
    public void Plan_ObjectStoreWork_SelectsRemoteNativeBackend()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.ObjectStoreCog,
            Cost(decodedBytes: 128 * MiB, scratchBytes: 256 * MiB, databaseWork: 5_000_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.Backend.Should().Be("aws-batch");
        decision.ReasonCode.Should().Be("native-remote-source-local");
    }

    [Fact]
    public void Plan_ModestInlineWork_SelectsLocalNativeWorker()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
        decision.ReasonCode.Should().Be("native-local-budget");
    }

    [Fact]
    public void Plan_HighScratchInlineWork_SelectsRemoteNativeBackend()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 128 * MiB, scratchBytes: 512 * MiB, databaseWork: 5_000_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.ReasonCode.Should().Be("native-remote-burst-isolation");
    }

    [Fact]
    public void Plan_DatabasePressure_SelectsSemanticallyCapableNativeWorker()
    {
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Health = Health(database: RasterDatabaseHealth.Pressured),
        };

        var decision = _sut.Plan(request);

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void Plan_DatabaseBudgetExceeded_SelectsSemanticallyCapableNativeWorker()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 600_000_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void Plan_UnknownCostWithoutRemoteBackend_RefusesActionably()
    {
        var request = Request(
            RasterInputResidency.Inline,
            new RasterCostEstimatorInput()) with
        {
            Health = Health(remoteAvailable: false),
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .Where(exception => exception.ReasonCode == "no-eligible-raster-placement")
            .WithMessage("*incomplete*remote backend*");
    }

    [Fact]
    public void Plan_ExternalSourceWithoutRemoteBackend_RefusesActionably()
    {
        var request = Request(
            RasterInputResidency.ObjectStoreCog,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Health = Health(remoteAvailable: false),
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .WithMessage("*external raster sources*remote native backend*");
    }

    [Fact]
    public void Plan_RequiredUnavailablePlacement_DoesNotSilentlyOverridePolicy()
    {
        var request = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Policy = Policy(requiredPlacement: RasterExecutionPlacement.RemoteBackend),
            Health = Health(remoteAvailable: false),
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .Where(exception => exception.ReasonCode == "no-eligible-raster-placement");
    }

    [Fact]
    public void Plan_EngineDisabledByPolicy_SelectsAllowedEngineOnly()
    {
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Policy = Policy(allowedEngines: [RasterEngine.GdalNative]),
        };

        var decision = _sut.Plan(request);

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void Plan_SameImmutableSnapshots_IsIdempotent()
    {
        var request = Request(
            RasterInputResidency.ObjectStoreCog,
            Cost(decodedBytes: 128 * MiB, scratchBytes: 256 * MiB, databaseWork: 5_000_000));

        _sut.Plan(request).Should().BeEquivalentTo(_sut.Plan(request));
    }

    [Fact]
    public void Plan_MutatingRetry_ReusesPinnedDecisionDespiteChangedHealth()
    {
        var initial = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)));
        var retry = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            ExistingDecision = initial,
            MutatingAttemptStarted = true,
            Health = Health(localAvailable: false, remoteAvailable: true),
            Policy = Policy(requiredPlacement: RasterExecutionPlacement.RemoteBackend),
        };

        _sut.Plan(retry).Should().BeSameAs(initial);
    }

    [Fact]
    public void Plan_MutatingRetryWithInvalidPinnedPlacement_RejectsBeforeReuse()
    {
        var initial = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)));
        var retry = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            ExistingDecision = initial with { Placement = RasterExecutionPlacement.DurablePostgis },
            MutatingAttemptStarted = true,
        };

        var act = () => _sut.Plan(retry);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*invalid engine/placement binding*");
    }

    [Fact]
    public void Plan_RegistryCapabilityFailure_IsReportedBeforePlacement()
    {
        var planner = new RasterExecutionPlanner(
            new RasterEngineCapabilityRegistry(),
            NullLogger<RasterExecutionPlanner>.Instance);
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Policy = Policy(requiredEngine: RasterEngine.Postgis),
        };

        var act = () => planner.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .WithMessage("*No canonical PostGIS raster IProcessExecutor*");
    }

    [Fact]
    public void Plan_UndefinedSnapshotEnum_RejectsBeforeCapabilityLookup()
    {
        var request = Request(
            (RasterInputResidency)999,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000));

        var act = () => _sut.Plan(request);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*undefined enum value*");
    }

    [Fact]
    public void RequestFactory_LegacyCatalogReference_ClassifiesObjectStoreWithoutMaterializing()
    {
        var process = CreateRegistry().Find("raster.clip")!;
        var plan = new AnalysisPlan
        {
            PlanId = "plan-catalog-cog",
            IntentId = "intent-catalog-cog",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "raster.clip",
                    Inputs = new Dictionary<string, string> { ["rasterId"] = "42" },
                },
            ],
        };

        var request = RasterExecutionPlanningRequestFactory.Create(
            plan,
            process,
            new RasterExecutionPlannerOptions(),
            remoteBackendAvailable: false,
            remoteBackend: null);

        request.InputResidencies.Should().Equal(RasterInputResidency.ObjectStoreCog);
        request.Cost.DecodedBytes.Should().BeNull();
        request.Cost.ExpectedScratchBytes.Should().BeNull();
        plan.Steps[0].Inputs.Should().NotContainKey("source");
    }

    private const long MiB = 1024L * 1024L;

    private static RasterExecutionPlanningRequest Request(
        RasterInputResidency residency,
        RasterCostEstimatorInput cost) => new()
        {
            ProcessId = "raster.clip",
            InputResidencies = [residency],
            InputMediaTypes = ["image/tiff"],
            OutputSink = RasterOutputSink.JobArtifact,
            Cost = cost,
            Budgets = Budgets(),
            Health = Health(),
            Policy = Policy(),
        };

    private static RasterCostEstimatorInput Cost(long decodedBytes, long scratchBytes, long databaseWork) => new()
    {
        SourceCount = 1,
        BandCount = 1,
        ZoneCount = 0,
        InputPixels = decodedBytes / 8,
        OutputPixels = decodedBytes / 8,
        DecodedBytes = decodedBytes,
        ExpectedScratchBytes = scratchBytes,
        ExpectedDatabaseWork = databaseWork,
    };

    private static RasterExecutionBudgetSnapshot Budgets() => new()
    {
        Version = "config-v1",
        MaxRequestDecodedBytes = 64 * MiB,
        MaxRequestScratchBytes = 128 * MiB,
        MaxRequestDatabaseWork = 10_000_000,
        MaxDatabaseDecodedBytes = 512 * MiB,
        MaxDatabaseScratchBytes = 1024 * MiB,
        MaxDatabaseWork = 500_000_000,
        MaxLocalDecodedBytes = 64 * MiB,
        MaxLocalScratchBytes = 128 * MiB,
    };

    private static RasterExecutionHealthSnapshot Health(
        RasterDatabaseHealth database = RasterDatabaseHealth.Healthy,
        bool localAvailable = true,
        bool remoteAvailable = true) => new()
        {
            Version = "health-v1",
            Database = database,
            LocalNativeWorkerAvailable = localAvailable,
            RemoteNativeBackendAvailable = remoteAvailable,
            RemoteBackend = remoteAvailable ? "aws-batch" : null,
        };

    private static RasterExecutionPolicySnapshot Policy(
        RasterEngine? requiredEngine = null,
        RasterExecutionPlacement? requiredPlacement = null,
        IReadOnlyList<RasterEngine>? allowedEngines = null) => new()
        {
            PolicyRef = "test-policy",
            AllowedEngines = allowedEngines ?? [RasterEngine.Postgis, RasterEngine.GdalNative],
            AllowedPlacements =
            [
                RasterExecutionPlacement.Request,
                RasterExecutionPlacement.DurablePostgis,
                RasterExecutionPlacement.LocalNativeWorker,
                RasterExecutionPlacement.RemoteBackend,
            ],
            RequiredEngine = requiredEngine,
            RequiredPlacement = requiredPlacement,
        };

    private static RasterEngineCapabilityRegistry CreateRegistry() =>
        new RasterEngineCapabilityRegistry(
        [
            new RasterProcessCapability
            {
                ProcessId = "raster.clip",
                SemanticVersion = "1.0.0",
                Engines =
                [
                    Capability(
                        RasterEngine.Postgis,
                        RasterEngineDefaultPreference.Preferred,
                        [RasterInputResidency.Postgis]),
                    Capability(
                        RasterEngine.GdalNative,
                        RasterEngineDefaultPreference.Fallback,
                        [
                            RasterInputResidency.Postgis,
                            RasterInputResidency.ObjectStoreCog,
                            RasterInputResidency.StagedArtifact,
                            RasterInputResidency.Inline,
                        ]),
                ],
            },
        ]);

    private static RasterEngineCapability Capability(
        RasterEngine engine,
        RasterEngineDefaultPreference preference,
        IReadOnlyList<RasterInputResidency> residencies) => new()
        {
            Engine = engine,
            ImplementationVersion = $"test.{engine}@1.0.0",
            RequiredCapabilities = ["raster.clip"],
            Formats = new RasterFormatRestrictions
            {
                InputMediaTypes = ["image/tiff"],
                OutputMediaTypes = ["image/tiff"],
            },
            InputResidencies = residencies,
            OutputSinks = [RasterOutputSink.JobArtifact],
            RequestExecutionAllowed = engine == RasterEngine.Postgis,
            DefaultPreference = preference,
            IsAvailable = true,
        };
}
