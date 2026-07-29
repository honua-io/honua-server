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
/// keyed on the live request: <c>AccessPolicyHelpers.EvaluateResourceAccessAsync</c>
/// needs the principal's role/grant claims, the tenant scope, and the request-scoped
/// permission resolver. A durable job persists only
/// <c>Audit.RequestedBy</c> — the submitter's ID string — so at execution time there is
/// no faithful principal left to authorize: reconstructing one (as the approval-resume
/// path does) yields a claims-free identity that carries none of the submitter's roles.
/// Evaluating the gate while the submitter's real principal is still in hand is both the
/// only correct evaluation and the better contract: the caller gets an immediate 401/403
/// instead of a queued job that can never succeed. This mirrors the synchronous handler,
/// which likewise authorizes once, at request time.
/// </para>
///
/// <para>
/// <b>Denial shape.</b> Every failure — layer missing, layer retired, tenant-invisible,
/// policy/grant denial, or an unevaluable (non-request) submission — collapses into the
/// same <see cref="GeoprocessingAuthorizationException"/> so a denial can never be used
/// to probe which layer ids exist. The exception is mapped to a protocol-appropriate
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
    private readonly ILogger<GeoprocessingJobService> _logger;

    /// <summary>
    /// Creates the gate over the ambient request accessor. The submitting principal is
    /// taken from the caller-supplied <see cref="ClaimsPrincipal"/>; the request context
    /// supplies the scoped services (metadata graph snapshot, permission resolver,
    /// tenant scope) the shared access pipeline resolves through.
    /// </summary>
    /// <param name="httpContextAccessor">Ambient request accessor.</param>
    /// <param name="logger">Logger bound to <see cref="GeoprocessingJobService"/> so denials share its category.</param>
    public GeoprocessingLayerAccessGuard(
        IHttpContextAccessor httpContextAccessor,
        ILogger<GeoprocessingJobService> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Enforces read access, for the submitting principal, on every catalog layer the
    /// plan's gated steps will read. Throws
    /// <see cref="GeoprocessingAuthorizationException"/> on the first denial.
    /// </summary>
    /// <param name="plan">The submitted analysis plan.</param>
    /// <param name="principal">The submitting principal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnsureLayerReadAccessAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var step in plan.Steps)
        {
            if (!string.Equals(step.ProcessId, EnrichmentJobExecutor.HandledProcessId, StringComparison.Ordinal))
            {
                continue;
            }

            await EnsureEnrichmentLayersReadableAsync(step, principal, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gates the two layers an <c>enrichment.enrich</c> job reads: the caller-selected
    /// source layer (absent when the job stages an inline FeatureCollection instead) and
    /// the resolved dataset's backing layer — exactly the pair
    /// <c>DataEnrichmentRequestHandlers.HandleEnrichPost</c> validates.
    /// </summary>
    private async Task EnsureEnrichmentLayersReadableAsync(
        AnalysisPlanStep step,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        // A staged inline source carries no layerId; the dataset layer is always read.
        var hasSourceLayer = TryReadLayerId(step, "layerId", out var sourceLayerId);

        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            // No request context means the submitter's grants, tenant scope, and
            // permission resolver are all unavailable, so the layer reads this job would
            // perform cannot be authorized against anyone. Fail closed rather than queue
            // an unauthorized read.
            Deny(principal, "submission has no request context");
            return;
        }

        if (hasSourceLayer)
        {
            await EnsureLayerReadableAsync(context, principal, sourceLayerId, cancellationToken).ConfigureAwait(false);
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
            return;
        }

        var dataset = await resolver.ResolveAsync(datasetId, cancellationToken).ConfigureAwait(false);
        if (dataset is null)
        {
            return;
        }

        await EnsureLayerReadableAsync(context, principal, dataset.LayerId, cancellationToken).ConfigureAwait(false);
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
        Deny(principal, "layer read access denied", result.NotFound ? null : result.Decision.RequiresAuthentication);
    }

    private void Deny(ClaimsPrincipal principal, string reason, bool? requiresAuthentication = null)
    {
        var needsAuth = requiresAuthentication ?? principal.Identity?.IsAuthenticated != true;

        GeoprocessingServiceLog.LayerReadAccessDenied(_logger, reason);
        throw new GeoprocessingAuthorizationException(
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
