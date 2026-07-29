// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Execution;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

/// <summary>
/// Submission-time layer read-access gate for layer-sourced geoprocessing plans
/// (honua-server#2283 review). <c>Process.Execute</c> authorizes the ACT of running a
/// process; it says nothing about the specific catalog layers the process will read.
/// The synchronous enrichment endpoint (<c>POST /api/enrich</c>) therefore calls
/// <c>LayerValidationHelpers.ValidateLayerWithAccessV2Async</c> at <c>AccessScope.Read</c>
/// for BOTH the caller-selected source layer and the enrichment dataset's backing
/// layer before reading either one. Without this gate the asynchronous
/// <c>enrichment.enrich</c> job would read the same two layers through
/// <c>source.honua-layer</c> with no per-layer evaluation of the submitting
/// principal, so a caller holding only <c>Process.Execute</c> could exfiltrate a
/// layer it cannot read on any protocol surface.
///
/// <para>
/// <b>Why the gate runs at SUBMISSION, not at execution.</b> The access pipeline is
/// keyed on the submitting principal's role/grant claims plus the scoped services
/// (metadata graph snapshot, permission resolver, tenant scope) that
/// <c>AccessPolicyHelpers.EvaluateResourceAccessAsync</c> resolves through. A durable
/// job persists only <c>Audit.RequestedBy</c> — the submitter's ID string — so at
/// execution time there is no faithful principal left to authorize: reconstructing one
/// (as the approval-resume path does) yields a claims-free identity that carries none
/// of the submitter's roles. Evaluating the gate while the submitter's real principal is
/// still in hand is both the only correct evaluation and the better contract: the caller
/// gets an immediate 401/403 instead of a queued job that can never succeed. This
/// mirrors the synchronous handler, which likewise authorizes once, at request time.
/// </para>
///
/// <para>
/// <b>Contextless (background) submissions.</b> The gate is NOT an HTTP-only control:
/// <c>SubmitJobAsync</c> is also called from in-process background paths that carry a
/// principal but no ambient <see cref="HttpContext"/> — most importantly the workflow
/// reconcile tick, which submits each step under
/// <c>OrchestrationSystemPrincipal</c> (honua-server#3043 review). Treating "no ambient
/// request" as unevaluable-and-therefore-denied would reject every workflow-dispatched
/// <c>enrichment.enrich</c> step, so those submissions instead get a synthesized
/// evaluation context: a <see cref="DefaultHttpContext"/> whose
/// <see cref="HttpContext.User"/> is the SUBMITTING principal and whose
/// <see cref="HttpContext.RequestServices"/> is a fresh DI scope. The shared pipeline
/// then evaluates the submitter's own grants/policies exactly as it would for a request,
/// so nothing is waved through by the mere absence of a request: a background submission
/// under a restricted principal is still denied, while the orchestrator's system identity
/// passes on the authority it actually holds. Two consequences are deliberate: a fresh
/// scope resolves no <c>ITenantContext</c>, so a TENANT-SCOPED layer is invisible and
/// therefore denied on this path (fail closed — the background read would not be
/// tenant-correct either), and when no scope factory is available at all the gate has no
/// way to evaluate anything and denies. Workflow authoring is additionally gated against
/// the human requester by <c>GeoprocessingJobService.EnsurePlanExecutionTierAuthorizedAsync</c>,
/// which <c>WorkflowOrchestrationEngine.CreateRunAsync</c> calls per step with the live
/// request principal (the honua-server#2798 pattern).
/// </para>
///
/// <para>
/// <b>Binding the authorized layer to the job.</b> The gate authorizes the dataset's
/// backing layer AS RESOLVED AT SUBMISSION. An admin can re-point a managed enrichment
/// dataset at a different layer while the job is queued, so the executor must not simply
/// re-resolve and read whatever is current. The gate therefore stamps the authorized
/// layer identity onto the step as
/// <see cref="EnrichmentJobExecutor.AuthorizedDatasetLayerInput"/> (always overwriting
/// any caller-supplied value — the gate is the only writer), and
/// <see cref="EnrichmentJobExecutor"/> fails the job when the re-resolved dataset no
/// longer matches that binding.
/// </para>
///
/// <para>
/// <b>Denial shape.</b> Every failure — layer missing, layer retired, tenant-invisible,
/// policy/grant denial, or an unevaluable submission — collapses into the same
/// <see cref="GeoprocessingAuthorizationException"/> so a denial can never be used to
/// probe which layer ids exist. The exception is mapped to a protocol-appropriate
/// 401/403 by the shared adapter error paths, and carries no SQL, provider, or
/// filesystem detail.
/// </para>
///
/// <para>
/// Scope: this change gates <c>enrichment.enrich</c> (the process introduced by #2283).
/// The same class of gap for the other layer-sourced managed processes is tracked
/// separately as honua-server#3046, which extends the layer enumeration below.
/// </para>
/// </summary>
internal sealed class GeoprocessingLayerAccessGuard
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly ILogger<GeoprocessingJobService> _logger;

    /// <summary>
    /// Creates the gate over the ambient request accessor plus a scope factory used to
    /// synthesize an evaluation context for contextless (background/orchestration)
    /// submissions. The evaluated principal is always the caller-supplied
    /// <see cref="ClaimsPrincipal"/>; the context only supplies the scoped services the
    /// shared access pipeline resolves through (metadata graph snapshot, permission
    /// resolver, tenant scope).
    /// </summary>
    /// <param name="httpContextAccessor">Ambient request accessor.</param>
    /// <param name="serviceScopeFactory">
    /// Scope factory for contextless submissions. When absent, a submission with no
    /// ambient request cannot be evaluated at all and is denied.
    /// </param>
    /// <param name="logger">Logger bound to <see cref="GeoprocessingJobService"/> so denials share its category.</param>
    public GeoprocessingLayerAccessGuard(
        IHttpContextAccessor httpContextAccessor,
        IServiceScopeFactory? serviceScopeFactory,
        ILogger<GeoprocessingJobService> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Enforces read access, for the submitting principal, on every catalog layer the
    /// plan's gated steps will read, and returns the plan with the authorized dataset
    /// layer identity bound to each gated step. Throws
    /// <see cref="GeoprocessingAuthorizationException"/> on the first denial.
    /// </summary>
    /// <param name="plan">The submitted analysis plan.</param>
    /// <param name="principal">The submitting principal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The plan to submit: the input instance when it carries no gated step, otherwise a
    /// copy whose gated steps carry the authorized dataset-layer binding.
    /// </returns>
    public async Task<AnalysisPlan> EnsureLayerReadAccessAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(principal);

        AnalysisPlanStep[]? boundSteps = null;
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            if (!string.Equals(step.ProcessId, EnrichmentJobExecutor.HandledProcessId, StringComparison.Ordinal))
            {
                continue;
            }

            var authorizedDatasetLayerId =
                await EnsureEnrichmentLayersReadableAsync(step, principal, cancellationToken).ConfigureAwait(false);

            boundSteps ??= [.. plan.Steps];
            boundSteps[index] = BindAuthorizedDatasetLayer(step, authorizedDatasetLayerId);
        }

        return boundSteps is null ? plan : plan with { Steps = boundSteps };
    }

    /// <summary>
    /// Gates the two layers an <c>enrichment.enrich</c> job reads: the caller-selected
    /// source layer (absent when the job stages an inline FeatureCollection instead) and
    /// the resolved dataset's backing layer — exactly the pair
    /// <c>DataEnrichmentRequestHandlers.HandleEnrichPost</c> validates. Returns the
    /// authorized dataset layer id, or <see langword="null"/> when no dataset resolves
    /// (the executor fails such a job before any layer read).
    /// </summary>
    private async Task<int?> EnsureEnrichmentLayersReadableAsync(
        AnalysisPlanStep step,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        // A staged inline source carries no layerId; the dataset layer is always read.
        var hasSourceLayer = TryReadLayerId(step, "layerId", out var sourceLayerId);

        var ambient = _httpContextAccessor.HttpContext;
        if (ambient is null && _serviceScopeFactory is null)
        {
            // Neither a live request nor a way to build a scope: the submitter's grants,
            // tenant scope, and permission resolver are all unreachable, so the layer
            // reads this job would perform cannot be authorized against anyone. Fail
            // closed rather than queue an unauthorized read.
            throw Deny(principal, "submission has no evaluable authorization context");
        }

        // Contextless submissions (workflow reconcile tick, other background dispatchers)
        // are evaluated against the SUBMITTING principal over a fresh scope rather than
        // waved through; see the type remarks.
        IServiceScope? ownedScope = null;
        HttpContext context;
        if (ambient is not null)
        {
            context = ambient;
        }
        else
        {
            ownedScope = _serviceScopeFactory!.CreateScope();
            context = new DefaultHttpContext
            {
                RequestServices = ownedScope.ServiceProvider,
                User = principal
            };
        }

        try
        {
            if (hasSourceLayer)
            {
                await EnsureLayerReadableAsync(context, principal, sourceLayerId, cancellationToken)
                    .ConfigureAwait(false);
            }

            // The dataset's backing layer is resolved through the SAME neutral catalog seam
            // the executor uses, so the gate authorizes the layer the job will actually read
            // rather than a re-derived guess. When no dataset resolves (or no catalog is
            // registered at all) the executor fails the job before any layer read, so there
            // is nothing to authorize here.
            var resolver = context.RequestServices.GetService<IEnrichmentDatasetResolver>();
            if (resolver is null
                || !step.Inputs.TryGetValue("datasetId", out var datasetId)
                || string.IsNullOrWhiteSpace(datasetId))
            {
                return null;
            }

            var dataset = await resolver.ResolveAsync(datasetId, cancellationToken).ConfigureAwait(false);
            if (dataset is null)
            {
                return null;
            }

            await EnsureLayerReadableAsync(context, principal, dataset.LayerId, cancellationToken)
                .ConfigureAwait(false);
            return dataset.LayerId;
        }
        finally
        {
            ownedScope?.Dispose();
        }
    }

    /// <summary>
    /// Evaluates one layer through the shared metadata-v2 resolution + access pipeline
    /// (per-operation grants first, coarse access policy as the fallback) — the same seam
    /// the HTTP layer validators and the gRPC adapters use.
    /// </summary>
    private async Task EnsureLayerReadableAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        int layerId,
        CancellationToken cancellationToken)
    {
        var result = await LayerValidationHelpers.EvaluateLayerReadAccessV2Async(
            context, layerId, requiredProtocol: null, cancellationToken).ConfigureAwait(false);

        if (!result.NotFound && result.Decision.IsAllowed)
        {
            return;
        }

        // A missing/retired layer is reported as the SAME denial as a policy denial (and
        // with the authentication requirement derived from the principal, not from a
        // resolved policy) so the response cannot be used to distinguish "layer exists but
        // is protected" from "layer does not exist".
        throw Deny(principal, "layer read access denied", result.NotFound ? null : result.Decision.RequiresAuthentication);
    }

    /// <summary>
    /// Stamps the authorized dataset layer identity onto a gated step, always REPLACING
    /// any caller-supplied value so the binding can only ever come from this gate. A step
    /// whose dataset did not resolve carries no binding, and the executor fails it.
    /// </summary>
    private static AnalysisPlanStep BindAuthorizedDatasetLayer(AnalysisPlanStep step, int? authorizedDatasetLayerId)
    {
        var inputs = new Dictionary<string, string>(step.Inputs.Count + 1, StringComparer.Ordinal);
        foreach (var input in step.Inputs)
        {
            if (string.Equals(input.Key, EnrichmentJobExecutor.AuthorizedDatasetLayerInput, StringComparison.Ordinal))
            {
                continue;
            }

            inputs[input.Key] = input.Value;
        }

        if (authorizedDatasetLayerId is { } layerId)
        {
            inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput] =
                layerId.ToString(CultureInfo.InvariantCulture);
        }

        return step with { Inputs = inputs };
    }

    private GeoprocessingAuthorizationException Deny(
        ClaimsPrincipal principal,
        string reason,
        bool? requiresAuthentication = null)
    {
        var needsAuth = requiresAuthentication ?? principal.Identity?.IsAuthenticated != true;

        GeoprocessingServiceLog.LayerReadAccessDenied(_logger, reason);
        return new GeoprocessingAuthorizationException(
            needsAuth,
            needsAuth
                ? "Authentication is required for this operation."
                : "You do not have permission to read one or more layers this process would read.",
            OperatorResourceType.Catalog,
            OperatorOperation.Read);
    }

    private static bool TryReadLayerId(AnalysisPlanStep step, string key, out int layerId)
    {
        layerId = 0;
        return step.Inputs.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId)
            && layerId >= 0;
    }
}
