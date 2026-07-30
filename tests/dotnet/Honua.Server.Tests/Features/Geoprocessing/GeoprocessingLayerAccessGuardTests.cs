// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Unit coverage for the submit-time per-layer read gate
/// (<see cref="GeoprocessingLayerAccessGuard"/>, #2283) with a focus on the
/// CONTEXTLESS submission path introduced by the #3043 review: the workflow reconcile
/// tick and other background dispatchers call
/// <c>IGeoprocessingJobService.SubmitJobAsync</c> from a timer tick with a synthesized
/// orchestration principal and no ambient <see cref="HttpContext"/>. The gate must
/// evaluate that principal's real authority (so a legitimate orchestration dispatch is
/// not rejected and its workflow can execute) while still refusing a background
/// submission whose principal cannot read the layer — absence of a request must never be
/// a bypass.
///
/// <para>
/// It must also treat the dataset-layer binding as a PIN set by a live requester: enforced
/// on every later authorization, never re-derived. The reconcile tick submits under an
/// identity carrying the wildcard-granted <c>admin</c> role, so re-deriving the binding
/// there would authorize whatever layer a re-pointed dataset resolves to and would defeat
/// the human requester's authorization entirely.
/// </para>
/// </summary>
public sealed class GeoprocessingLayerAccessGuardTests
{
    private const int SourceLayerId = 7;
    private const int DatasetLayerId = 8;

    /// <summary>Layer an admin re-points the enrichment dataset at after publication.</summary>
    private const int RepointedDatasetLayerId = 9;

    private const string DatasetId = "test-boundaries";

    /// <summary>Role the seeded source/dataset layers' access policies admit.</summary>
    private const string ReaderRole = "layer-reader";

    /// <summary>
    /// Role admitted ONLY by <see cref="RepointedDatasetLayerId"/>. The orchestrator identity
    /// holds it; the human requester does not.
    /// </summary>
    private const string RepointedLayerRole = "repointed-layer-reader";

    /// <summary>
    /// Regression for the #3043 review finding: <c>WorkflowOrchestrationEngine</c> submits
    /// each step from a background reconcile tick under
    /// <c>OrchestrationSystemPrincipal</c> — an authenticated principal with no ambient
    /// request. Before the fix the gate treated "no ambient request" as unevaluable and
    /// denied, so every workflow-dispatched <c>enrichment.enrich</c> step failed at
    /// submission and the workflow could never execute. It is accepted while carrying the
    /// requester-authorized dataset-layer pin the stored workflow definition supplies.
    /// </summary>
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

    /// <summary>
    /// Fail-closed half of the same finding: a background submission with NO
    /// requester-authorized binding is refused. There is no live requester to authorize on
    /// that path, and the submitting orchestrator identity carries <c>admin</c> (which
    /// <c>InMemoryRoleStore</c> seeds with a wildcard <c>*:*:*</c> grant), so deriving the
    /// binding there would authorize the current dataset layer unconditionally — the exact
    /// escalation the pin exists to prevent. The binding must come from an authoring-time
    /// authorization instead.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequestAndNoRequesterBinding_Denies()
    {
        var guard = BuildGuard(out _);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            EnrichmentPlan(), OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    /// <summary>
    /// The contextless path is an evaluation, not an exemption: a background submission
    /// whose principal holds no role the layer admits is still denied even when it carries a
    /// matching pin, so "no HttpContext" can never be used to bypass the gate that closed
    /// the original P1.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequest_UnauthorizedPrincipalIsStillDenied()
    {
        var guard = BuildGuard(out _);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            PinnedEnrichmentPlan(DatasetLayerId), ProcessExecuteOnlyPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    /// <summary>
    /// The dataset's backing layer is resolved from the catalog, never named by the caller,
    /// so it is the layer a caller-parameter check would miss. A background submission whose
    /// principal cannot read it is refused even when the caller-selected source layer is
    /// readable.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequest_DeniesWhenOnlyTheDatasetLayerIsRestricted()
    {
        var guard = BuildGuard(out _, datasetLayerRoles: ["someone-else"]);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            PinnedEnrichmentPlan(DatasetLayerId), OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    /// <summary>
    /// With no scope factory there is no way to reach the metadata graph, permission
    /// resolver, or access policies at all, so the gate has nothing to evaluate and denies —
    /// even for a submission that carries the requester-authorized pin.
    /// </summary>
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

    /// <summary>
    /// The P1 this closes, end to end. A human who may read only
    /// <see cref="DatasetLayerId"/> authorizes the workflow's enrichment step at publication
    /// time, which pins that layer onto the stored plan. An admin then re-points the managed
    /// dataset at <see cref="RepointedDatasetLayerId"/> — a layer the human cannot read but
    /// the orchestrator identity can — and the reconcile tick dispatches the step from a
    /// background context. The dispatch must FAIL on the pin instead of re-authorizing and
    /// reading the new layer as the orchestrator.
    ///
    /// <para>
    /// The positive control makes the denial attributable to the pin alone: the SAME
    /// contextless dispatch, by the SAME principal, against the SAME re-pointed dataset is
    /// accepted when the plan is pinned to the layer the dataset now resolves to. So the
    /// orchestrator demonstrably can read layer <see cref="RepointedDatasetLayerId"/> — what
    /// stops it in the first phase is that its human requester never authorized that layer.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The pin is matched on the ambient path too, so an admin who re-points the dataset
    /// after publication cannot have a HUMAN-created run of that workflow read the new layer
    /// either — run creation is refused up front instead.
    /// </summary>
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

    /// <summary>
    /// A pin whose dataset no longer resolves at all cannot be matched against anything, so
    /// the submission fails closed instead of being queued as unverifiable.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_PinnedPlanWhoseDatasetNoLongerResolves_Denies()
    {
        var guard = BuildGuard(out _);

        var plan = PinnedEnrichmentPlan(DatasetLayerId, datasetId: "removed-dataset");

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            plan, OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    /// <summary>
    /// A plan with no layer-sourced step is returned untouched — the gate never rewrites
    /// plans it does not gate.
    /// </summary>
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

    /// <summary>
    /// A caller-supplied binding can only ever CONSTRAIN the submission, never widen it: one
    /// that names a layer the dataset does not resolve to is refused rather than honoured, so
    /// a forged value cannot pre-authorize a different layer — it can only get its own
    /// submission denied.
    /// </summary>
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

    /// <summary>
    /// The stamped value is still gate-owned: the gate rebuilds the step inputs from the
    /// layer it resolved and authorized, so the persisted binding is never the caller's
    /// string (even a non-canonical spelling of the right layer is normalized).
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_MatchingBinding_IsRewrittenFromTheAuthorizedLayer()
    {
        var guard = BuildGuard(out _);

        var plan = EnrichmentPlan(extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] = "+8",
        });

        var bound = await guard.EnsureLayerReadAccessAsync(plan, OrchestrationPrincipal(), CancellationToken.None);

        bound.Steps[0].Inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput]
            .Should().Be(DatasetLayerId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The ambient-request path keeps evaluating against the live request, so the original
    /// P1 fix is unaffected by the contextless branch.
    /// </summary>
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

    /// <summary>
    /// The shared access pipeline resolves the metadata graph and the coarse access-policy
    /// evaluator from the request services. No <c>IPermissionResolver</c> is registered, so
    /// grant evaluation reports no grant and the decision falls through to the layers'
    /// <see cref="AccessPolicy"/> — the same fall-through the
    /// <c>ProcessExecuteOnlyRoleStore</c> integration tests exercise.
    /// </summary>
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

    private static MetadataV2Resource Resource(string id, string name, string[] allowedRoles) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
        AccessPolicy = new AccessPolicy { AllowedRoles = allowedRoles },
    };

    private static MetadataV2Publication Publication(
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
        };

    /// <summary>
    /// The plan a workflow dispatch carries: an enrichment step already pinned to the dataset
    /// layer a live requester authorized (what publication persists onto the stored plan).
    /// </summary>
    private static AnalysisPlan PinnedEnrichmentPlan(int authorizedLayerId, string? datasetId = null)
        => EnrichmentPlan(
            extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] =
                    authorizedLayerId.ToString(CultureInfo.InvariantCulture),
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

    /// <summary>Serves one fixed graph snapshot to the shared access pipeline.</summary>
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

    /// <summary>
    /// Serves one enrichment dataset whose backing layer can be re-pointed mid-test, standing
    /// in for an admin editing a managed dataset after a workflow was published.
    /// </summary>
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

    /// <summary>
    /// Mirrors <c>OrchestrationSystemPrincipal.Create</c>: an authenticated identity whose
    /// authentication type is the orchestrator scheme, carrying the roles a workflow step
    /// submission runs under.
    /// </summary>
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

    /// <summary>
    /// The human who publishes the workflow: may read the source layer and the dataset's
    /// backing layer as it stands at publication, and nothing else.
    /// </summary>
    private static ClaimsPrincipal HumanRequesterPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "analyst"),
                new Claim(ClaimTypes.Role, ReaderRole),
            ],
            "TestScheme"));

    /// <summary>
    /// An authenticated caller holding process-execution authority but no role any layer
    /// admits — the principal the original #2283 finding describes.
    /// </summary>
    private static ClaimsPrincipal ProcessExecuteOnlyPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "execute-only"),
                new Claim(ClaimTypes.Role, "process-executor"),
            ],
            "TestScheme"));
}
