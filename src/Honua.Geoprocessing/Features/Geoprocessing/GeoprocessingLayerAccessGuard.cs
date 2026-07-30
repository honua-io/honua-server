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
/// way to evaluate anything and denies.
/// </para>
///
/// <para>
/// <b>The dataset-layer binding, and why a background submission must carry one.</b>
/// The gate authorizes the dataset's backing layer AS RESOLVED AT AUTHORIZATION TIME and
/// stamps that layer identity onto the step as
/// <see cref="EnrichmentJobExecutor.AuthorizedDatasetLayerInput"/>;
/// <see cref="EnrichmentJobExecutor"/> then fails the job when the dataset it re-resolves
/// at execution no longer matches. That closes the queueing window for a direct job, but
/// a workflow re-enters the same TOCTOU class through a different door: the reconcile tick
/// submits under <c>OrchestrationSystemPrincipal</c>, which carries the <c>admin</c> role,
/// and <c>admin</c> holds a wildcard grant — so re-deriving the binding on that path would
/// re-authorize and re-stamp whatever layer the dataset points at NOW, under an
/// effectively omnipotent principal, and the executor would accept it. An admin who
/// re-points a managed dataset between publication and dispatch could therefore have a
/// workflow read a layer its human requester was never allowed to read
/// (honua-server#3043 review).
/// </para>
///
/// <para>
/// The binding is therefore treated as a PIN set by a live requester and enforced, never
/// re-derived, afterwards:
/// <list type="bullet">
/// <item><description>
/// When the step already carries a binding, the gate requires the dataset it resolves to
/// match it, and denies otherwise. A pre-existing value can only ever CONSTRAIN the
/// submission — the resolved layer must still pass the read gate for the submitting
/// principal — so a forged value cannot pre-authorize anything, it can only get its own
/// submission refused.
/// </description></item>
/// <item><description>
/// A contextless (background/orchestration) submission MUST carry one. There is no live
/// requester to authorize against on that path, so a missing pin is unevaluable and fails
/// closed rather than falling back to the system principal's authority.
/// </description></item>
/// <item><description>
/// The stamped value always comes from this gate (the step's own inputs are rebuilt from
/// the layer the gate resolved and authorized), so the gate remains the only writer.
/// </description></item>
/// </list>
/// The pin is produced where a human IS present:
/// <c>GeoprocessingJobService.EnsurePlanExecutionTierAuthorizedAsync</c> returns the bound
/// plan, and <c>WorkflowPackageService.PublishVersionAsync</c> persists those bound step
/// plans into the stored <c>WorkflowDefinition</c>. Every later run of that definition —
/// cron, event, or manual — dispatches from the stored plan, so the reconcile tick can
/// only ever submit the layer the publishing human was authorized to read.
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
    /// <c>DataEnrichmentRequestHandlers.HandleEnrichPost</c> validates. Enforces any
    /// requester-authorized dataset-layer pin the step already carries, and requires one on
    /// contextless submissions. Returns the authorized dataset layer id, or
    /// <see langword="null"/> when no dataset resolves and no pin has to be matched (the
    /// executor fails such a job before any layer read).
    /// </summary>
    private async Task<int?> EnsureEnrichmentLayersReadableAsync(
        AnalysisPlanStep step,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        // A staged inline source carries no layerId; the dataset layer is always read.
        var hasSourceLayer = TryReadLayerId(step, "layerId", out var sourceLayerId);

        // The dataset-layer pin a live requester's authorization already produced, when the
        // step carries one. It is only ever a CONSTRAINT: the layer it names must be the one
        // the dataset still resolves to, and that layer must independently pass the read gate
        // below for the submitting principal. See the type remarks.
        var hasRequesterBinding = TryReadLayerId(
            step, EnrichmentJobExecutor.AuthorizedDatasetLayerInput, out var requesterAuthorizedLayerId);

        var ambient = _httpContextAccessor.HttpContext;
        if (ambient is null)
        {
            if (!hasRequesterBinding)
            {
                // No live requester, and no pin a live requester left behind. Re-deriving the
                // binding here would authorize the CURRENT dataset layer under the submitting
                // background identity — for the workflow reconcile tick that identity carries
                // the wildcard-granted `admin` role, so the derivation would always succeed
                // and would defeat the human requester's authorization outright. Fail closed.
                throw Deny(principal, "background submission carries no requester-authorized dataset-layer binding");
            }

            if (_serviceScopeFactory is null)
            {
                // Neither a live request nor a way to build a scope: the submitter's grants,
                // tenant scope, and permission resolver are all unreachable, so the layer
                // reads this job would perform cannot be authorized against anyone. Fail
                // closed rather than queue an unauthorized read.
                throw Deny(principal, "submission has no evaluable authorization context");
            }
        }

        // Contextless submissions (workflow reconcile tick, other background dispatchers)
        // are evaluated against the SUBMITTING principal over a fresh scope rather than
        // waved through; see the type remarks.
        using IServiceScope? ownedScope = ambient is null ? _serviceScopeFactory!.CreateScope() : null;
        var context = ambient ?? new DefaultHttpContext
        {
            RequestServices = ownedScope!.ServiceProvider,
            User = principal
        };

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
        var dataset = resolver is null
                || !step.Inputs.TryGetValue("datasetId", out var datasetId)
                || string.IsNullOrWhiteSpace(datasetId)
            ? null
            : await resolver.ResolveAsync(datasetId, cancellationToken).ConfigureAwait(false);

        if (dataset is null)
        {
            // A step carrying a pin whose dataset no longer resolves cannot be matched
            // against anything — the dataset was removed or renamed after the requester
            // authorized it — so fail closed instead of queueing an unverifiable read.
            if (hasRequesterBinding)
            {
                throw Deny(principal, "requester-authorized enrichment dataset no longer resolves");
            }

            return null;
        }

        // The pin is enforced, never refreshed: a dataset re-pointed after the requester
        // authorized it fails the submission (and therefore the workflow step) exactly as a
        // dataset re-pointed after a direct job was queued fails that job in the executor.
        if (hasRequesterBinding && dataset.LayerId != requesterAuthorizedLayerId)
        {
            throw Deny(principal, "enrichment dataset layer changed since the requester authorized it");
        }

        await EnsureLayerReadableAsync(context, principal, dataset.LayerId, cancellationToken)
            .ConfigureAwait(false);
        return dataset.LayerId;
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
    /// Stamps the authorized dataset layer identity onto a gated step, always REPLACING the
    /// incoming value so the binding can only ever come from this gate. When the step
    /// arrived with a pin, the caller has already proven it matches the layer the gate
    /// resolved and authorized, so the rewrite is value-preserving; when it did not, this is
    /// the point at which a live requester's authorization becomes the pin every later
    /// (re-)authorization of that plan must match. A step whose dataset did not resolve
    /// carries no binding, and the executor fails it.
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
