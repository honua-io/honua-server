// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingLayerAccessGuardTests
{
    private const int SourceLayerId = 7;
    private const int DatasetLayerId = 8;

    private const int RepointedDatasetLayerId = 9;

    private const string DatasetId = "test-boundaries";

    private const string ReaderRole = "layer-reader";

    private const string RepointedLayerRole = "repointed-layer-reader";

    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequest_AuthorizedPrincipalIsNotDenied()
    {
        var guard = BuildGuard(out _);

        var plan = PinnedEnrichmentPlan(DatasetLayerId);

        var bound = await guard.EnsureLayerReadAccessAsync(plan, OrchestrationPrincipal(), CancellationToken.None);

        bound.Steps.Should().HaveCount(1);
        bound.Steps[0].Inputs.Should().ContainKey(EnrichmentJobExecutor.AuthorizedDatasetLayerInput)
            .WhoseValue.Should().Be(DatasetLayerId.ToString(CultureInfo.InvariantCulture));
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequestAndNoRequesterBinding_Denies()
    {
        var guard = BuildGuard(out _);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            EnrichmentPlan(), OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequest_UnauthorizedPrincipalIsStillDenied()
    {
        var guard = BuildGuard(out _);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            PinnedEnrichmentPlan(DatasetLayerId), ProcessExecuteOnlyPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequest_DeniesWhenOnlyTheDatasetLayerIsRestricted()
    {
        var guard = BuildGuard(out _, datasetLayerRoles: ["someone-else"]);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            PinnedEnrichmentPlan(DatasetLayerId), OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequestAndNoScopeFactory_Denies()
    {
        var guard = new GeoprocessingLayerAccessGuard(
            new HttpContextAccessor(),
            serviceScopeFactory: null,
            NullLogger<GeoprocessingJobService>.Instance);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            PinnedEnrichmentPlan(DatasetLayerId), OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_DatasetRepointedAfterTheRequesterAuthorizedIt_FailsTheWorkflowDispatch()
    {
        var accessor = new HttpContextAccessor();
        var resolver = new MutableDatasetResolver(DatasetLayerId);
        var guard = BuildGuard(out var services, accessor: accessor, resolver: resolver);

        // Phase 1 — publication: the human requester authorizes the step over a live request.
        // The gate pins the dataset layer it authorized onto the plan that gets stored.
        var human = HumanRequesterPrincipal();
        accessor.HttpContext = new DefaultHttpContext { RequestServices = services, User = human };
        var published = await guard.EnsureLayerReadAccessAsync(EnrichmentPlan(), human, CancellationToken.None);
        published.Steps[0].Inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput]
            .Should().Be(DatasetLayerId.ToString(CultureInfo.InvariantCulture));

        // Phase 2 — an admin re-points the managed dataset at a layer the human cannot read.
        resolver.LayerId = RepointedDatasetLayerId;

        // Phase 3 — the reconcile tick dispatches the stored plan: no ambient request, and a
        // principal that DOES hold a role the re-pointed layer admits.
        accessor.HttpContext = null;
        var dispatcher = OrchestrationPrincipal(RepointedLayerRole);

        var act = async () => await guard.EnsureLayerReadAccessAsync(published, dispatcher, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>(
            "the workflow step must fail rather than read a layer its human requester was never authorized to read");

        // Positive control: the identical dispatch is accepted once the pin names the layer
        // the dataset actually resolves to, so the refusal above is the pin and nothing else.
        var repinned = await guard.EnsureLayerReadAccessAsync(
            PinnedEnrichmentPlan(RepointedDatasetLayerId), dispatcher, CancellationToken.None);
        repinned.Steps[0].Inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput]
            .Should().Be(RepointedDatasetLayerId.ToString(CultureInfo.InvariantCulture));
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_AmbientRequest_DeniesWhenThePinNoLongerMatchesTheDataset()
    {
        var accessor = new HttpContextAccessor();
        var resolver = new MutableDatasetResolver(RepointedDatasetLayerId);
        var guard = BuildGuard(out var services, accessor: accessor, resolver: resolver);

        var principal = OrchestrationPrincipal(RepointedLayerRole);
        accessor.HttpContext = new DefaultHttpContext { RequestServices = services, User = principal };

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            PinnedEnrichmentPlan(DatasetLayerId), principal, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_PinnedPlanWhoseDatasetNoLongerResolves_Denies()
    {
        var guard = BuildGuard(out _);

        var plan = PinnedEnrichmentPlan(DatasetLayerId, datasetId: "removed-dataset");

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            plan, OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_NoGatedStep_ReturnsThePlanUnchanged()
    {
        var guard = BuildGuard(out _);

        var plan = new AnalysisPlan
        {
            PlanId = "plan-ungated",
            IntentId = "intent-ungated",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s0",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["distance"] = "10" },
                }
            ],
        };

        var result = await guard.EnsureLayerReadAccessAsync(plan, OrchestrationPrincipal(), CancellationToken.None);

        result.Should().BeSameAs(plan);
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_ForgedBindingForAnotherLayer_Denies()
    {
        var guard = BuildGuard(out _);

        var plan = EnrichmentPlan(extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] = "4242",
        });

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            plan, OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_MatchingBinding_IsRewrittenFromTheAuthorizedLayer()
    {
        var guard = BuildGuard(out _);

        var plan = EnrichmentPlan(extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] = "+8",
            [EnrichmentJobExecutor.AuthorizedSourceLayerInput] =
                SourceLayerId.ToString(CultureInfo.InvariantCulture),
        });

        var bound = await guard.EnsureLayerReadAccessAsync(plan, OrchestrationPrincipal(), CancellationToken.None);

        bound.Steps[0].Inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput]
            .Should().Be(DatasetLayerId.ToString(CultureInfo.InvariantCulture));
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_AmbientRequest_DeniesAnUnauthorizedRequestPrincipal()
    {
        var accessor = new HttpContextAccessor();
        var guard = BuildGuard(out var services, accessor: accessor);
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = ProcessExecuteOnlyPrincipal(),
        };

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            EnrichmentPlan(), ProcessExecuteOnlyPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeoprocessingLayerAccessGuard BuildGuard(
        out ServiceProvider services,
        IHttpContextAccessor? accessor = null,
        string[]? datasetLayerRoles = null,
        MutableDatasetResolver? resolver = null)
    {
        services = BuildServices(datasetLayerRoles ?? [ReaderRole], resolver ?? new MutableDatasetResolver(DatasetLayerId));
        return new GeoprocessingLayerAccessGuard(
            accessor ?? new HttpContextAccessor(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GeoprocessingJobService>.Instance);
    }

    private static ServiceProvider BuildServices(string[] datasetLayerRoles, MutableDatasetResolver resolver)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();

        services.AddSingleton<IMetadataV2GraphProvider>(
            new StubGraphProvider(BuildSnapshot(datasetLayerRoles)));

        services.AddSingleton<IEnrichmentDatasetResolver>(resolver);

        return services.BuildServiceProvider();
    }

    private static MetadataV2GraphSnapshot BuildSnapshot(string[] datasetLayerRoles)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-1", Name = "guard-tests" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

        var sourceResource = Resource("res-source", "source-layer", [ReaderRole]);
        var datasetResource = Resource("res-dataset", "dataset-layer", datasetLayerRoles);

        // The layer an admin re-points the dataset at: readable by the orchestrator identity,
        // NOT by the human requester, so a dispatch that re-derived its own binding would
        // succeed here while the requester's authorization said nothing about this layer.
        var repointedResource = Resource("res-repointed", "repointed-layer", [RepointedLayerRole]);

        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Services = [service],
            Resources = [sourceResource, datasetResource, repointedResource],
            Publications =
            [
                Publication("pub-source", service.Metadata.Id, sourceResource.Metadata.Id, SourceLayerId),
                Publication("pub-dataset", service.Metadata.Id, datasetResource.Metadata.Id, DatasetLayerId),
                Publication(
                    "pub-repointed", service.Metadata.Id, repointedResource.Metadata.Id, RepointedDatasetLayerId),
            ],
        };

        return new MetadataV2GraphSnapshot(graph, "\"guard-tests\"", DateTimeOffset.UnixEpoch);
    }

    private static MetadataV2Object Resource(string id, string name, string[] allowedRoles) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
        AccessPolicy = new AccessPolicy { AllowedRoles = allowedRoles },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2Layer Publication(
        string id,
        string serviceId,
        string resourceId,
        int layerIndex) => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            ServiceId = serviceId,
            ResourceId = resourceId,
            LayerIndex = layerIndex,
            IsPrimary = true,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

    [UnitTest]
    public async Task EnsureLayerReadAccess_BackgroundSubmissionWithoutASourcePin_Denies()
    {
        var guard = BuildGuard(out _);

        // Dataset pinned, source not — exactly the shape a dynamically bound source produces.
        var plan = EnrichmentPlan(extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] =
                DatasetLayerId.ToString(CultureInfo.InvariantCulture),
        });

        var act = async () =>
            await guard.EnsureLayerReadAccessAsync(plan, OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>(
            "no live requester ever authorized this source layer");
    }

    [UnitTest]
    public async Task EnsureLayerReadAccess_BackgroundSubmissionWhoseSourcePinNamesAnotherLayer_Denies()
    {
        // The dynamic case proper: the stored plan was pinned for one source layer and the
        // expanded step now names a different one. Matching the pin is what makes substitution
        // after authorization detectable.
        var guard = BuildGuard(out _);

        var plan = EnrichmentPlan(extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] =
                DatasetLayerId.ToString(CultureInfo.InvariantCulture),
            [EnrichmentJobExecutor.AuthorizedSourceLayerInput] =
                (SourceLayerId + 1).ToString(CultureInfo.InvariantCulture),
        });

        var act = async () =>
            await guard.EnsureLayerReadAccessAsync(plan, OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>(
            "the step names a source layer other than the one the requester authorized");
    }

    private static AnalysisPlan PinnedEnrichmentPlan(int authorizedLayerId, string? datasetId = null)
        => EnrichmentPlan(
            extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] =
                    authorizedLayerId.ToString(CultureInfo.InvariantCulture),
                [EnrichmentJobExecutor.AuthorizedSourceLayerInput] =
                    SourceLayerId.ToString(CultureInfo.InvariantCulture),
            },
            datasetId: datasetId);

    private static AnalysisPlan EnrichmentPlan(
        IReadOnlyDictionary<string, string>? extraInputs = null,
        string? datasetId = null)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["datasetId"] = datasetId ?? DatasetId,
            ["layerId"] = SourceLayerId.ToString(CultureInfo.InvariantCulture),
        };
        if (extraInputs is not null)
        {
            foreach (var (key, value) in extraInputs)
            {
                inputs[key] = value;
            }
        }

        return new AnalysisPlan
        {
            PlanId = "plan-enrich",
            IntentId = "intent-enrich",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s0",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = EnrichmentJobExecutor.HandledProcessId,
                    Inputs = inputs,
                }
            ],
        };
    }

    private sealed class StubGraphProvider : IMetadataV2GraphProvider
    {
        private readonly MetadataV2GraphSnapshot _snapshot;

        public StubGraphProvider(MetadataV2GraphSnapshot snapshot) => _snapshot = snapshot;

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(revision == _snapshot.Revision ? _snapshot : null);
    }

    private sealed class MutableDatasetResolver : IEnrichmentDatasetResolver
    {
        public MutableDatasetResolver(int layerId) => LayerId = layerId;

        public int LayerId { get; set; }

        public Task<EnrichmentDatasetDefinition?> ResolveAsync(
            string idOrKey,
            CancellationToken cancellationToken)
            => Task.FromResult<EnrichmentDatasetDefinition?>(
                string.Equals(idOrKey, DatasetId, StringComparison.OrdinalIgnoreCase)
                    ? new EnrichmentDatasetDefinition(
                        DatasetId,
                        "Test Boundaries",
                        "boundary",
                        LayerId,
                        "intersects",
                        DistanceMeters: null,
                        Attributes: ["name"],
                        Attribution: null,
                        MinimumEdition: HonuaEdition.Pro,
                        Source: "config")
                    : null);
    }

    private static ClaimsPrincipal OrchestrationPrincipal(params string[] extraRoles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "honua/orchestrator"),
            new(ClaimTypes.Role, "orchestrator"),
            new(ClaimTypes.Role, ReaderRole),
        };
        claims.AddRange(extraRoles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "HonuaOrchestrator"));
    }

    private static ClaimsPrincipal HumanRequesterPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "analyst"),
                new Claim(ClaimTypes.Role, ReaderRole),
            ],
            "TestScheme"));

    private static ClaimsPrincipal ProcessExecuteOnlyPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "execute-only"),
                new Claim(ClaimTypes.Role, "process-executor"),
            ],
            "TestScheme"));
}
