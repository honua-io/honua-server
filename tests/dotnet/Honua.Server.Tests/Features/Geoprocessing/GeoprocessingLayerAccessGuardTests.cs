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
using NSubstitute;

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
/// a bypass. It must also bind the authorized dataset layer to the queued job so a
/// dataset re-pointed after submission cannot be read.
/// </summary>
public sealed class GeoprocessingLayerAccessGuardTests
{
    private const int SourceLayerId = 7;
    private const int DatasetLayerId = 8;
    private const string DatasetId = "test-boundaries";

    /// <summary>Role the seeded layers' access policies admit.</summary>
    private const string ReaderRole = "layer-reader";

    /// <summary>
    /// Regression for the #3043 review finding: <c>WorkflowOrchestrationEngine</c> submits
    /// each step from a background reconcile tick under
    /// <c>OrchestrationSystemPrincipal</c> — an authenticated principal with no ambient
    /// request. Before the fix the gate treated "no ambient request" as unevaluable and
    /// denied, so every workflow-dispatched <c>enrichment.enrich</c> step failed at
    /// submission and the workflow could never execute.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequest_AuthorizedPrincipalIsNotDenied()
    {
        var guard = BuildGuard(out _);

        var plan = EnrichmentPlan();

        var bound = await guard.EnsureLayerReadAccessAsync(plan, OrchestrationPrincipal(), CancellationToken.None);

        bound.Steps.Should().HaveCount(1);
        bound.Steps[0].Inputs.Should().ContainKey(EnrichmentJobExecutor.AuthorizedDatasetLayerInput)
            .WhoseValue.Should().Be(DatasetLayerId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The contextless path is an evaluation, not an exemption: a background submission
    /// whose principal holds no role the layer admits is still denied, so "no HttpContext"
    /// can never be used to bypass the gate that closed the original P1.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequest_UnauthorizedPrincipalIsStillDenied()
    {
        var guard = BuildGuard(out _);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            EnrichmentPlan(), ProcessExecuteOnlyPrincipal(), CancellationToken.None);

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
            EnrichmentPlan(), OrchestrationPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    /// <summary>
    /// With no scope factory there is no way to reach the metadata graph, permission
    /// resolver, or access policies at all, so the gate has nothing to evaluate and denies.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_NoAmbientRequestAndNoScopeFactory_Denies()
    {
        var guard = new GeoprocessingLayerAccessGuard(
            new HttpContextAccessor(),
            serviceScopeFactory: null,
            NullLogger<GeoprocessingJobService>.Instance);

        var act = async () => await guard.EnsureLayerReadAccessAsync(
            EnrichmentPlan(), OrchestrationPrincipal(), CancellationToken.None);

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
    /// The binding is authoritative and gate-owned: a caller-supplied
    /// <c>authorizedDatasetLayerId</c> is stripped and replaced with the layer the gate
    /// actually authorized, so a forged value cannot pre-authorize a different layer.
    /// </summary>
    [UnitTest]
    public async Task EnsureLayerReadAccess_CallerSuppliedBinding_IsReplacedWithTheAuthorizedLayer()
    {
        var guard = BuildGuard(out _);

        var plan = EnrichmentPlan(extraInputs: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnrichmentJobExecutor.AuthorizedDatasetLayerInput] = "4242",
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
        string[]? datasetLayerRoles = null)
    {
        services = BuildServices(datasetLayerRoles ?? [ReaderRole]);
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
    private static ServiceProvider BuildServices(string[] datasetLayerRoles)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();

        services.AddSingleton<IMetadataV2GraphProvider>(
            new StubGraphProvider(BuildSnapshot(datasetLayerRoles)));

        var resolver = Substitute.For<IEnrichmentDatasetResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<EnrichmentDatasetDefinition?>(
                string.Equals(callInfo.ArgAt<string>(0), DatasetId, StringComparison.OrdinalIgnoreCase)
                    ? new EnrichmentDatasetDefinition(
                        DatasetId,
                        "Test Boundaries",
                        "boundary",
                        DatasetLayerId,
                        "intersects",
                        DistanceMeters: null,
                        Attributes: ["name"],
                        Attribution: null,
                        MinimumEdition: HonuaEdition.Pro,
                        Source: "config")
                    : null));
        services.AddSingleton(resolver);

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

        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Services = [service],
            Resources = [sourceResource, datasetResource],
            Publications =
            [
                Publication("pub-source", service.Metadata.Id, sourceResource.Metadata.Id, SourceLayerId),
                Publication("pub-dataset", service.Metadata.Id, datasetResource.Metadata.Id, DatasetLayerId),
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

    private static AnalysisPlan EnrichmentPlan(IReadOnlyDictionary<string, string>? extraInputs = null)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["datasetId"] = DatasetId,
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
    /// Mirrors <c>OrchestrationSystemPrincipal.Create</c>: an authenticated identity whose
    /// authentication type is the orchestrator scheme, carrying the roles a workflow step
    /// submission runs under.
    /// </summary>
    private static ClaimsPrincipal OrchestrationPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "honua/orchestrator"),
                new Claim(ClaimTypes.Role, "orchestrator"),
                new Claim(ClaimTypes.Role, ReaderRole),
            ],
            "HonuaOrchestrator"));

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
