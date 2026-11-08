// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Execution;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

// Access is decided at submission because execution has only the submitter id, and a stored pin is never re-derived under the orchestrator admin role.
internal sealed class GeoprocessingLayerAccessGuard
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly ILogger<GeoprocessingJobService> _logger;

    public GeoprocessingLayerAccessGuard(
        IHttpContextAccessor httpContextAccessor,
        IServiceScopeFactory? serviceScopeFactory,
        ILogger<GeoprocessingJobService> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

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

            var authorized =
                await EnsureEnrichmentLayersReadableAsync(step, principal, cancellationToken).ConfigureAwait(false);

            boundSteps ??= [.. plan.Steps];
            boundSteps[index] = BindAuthorizedLayers(step, authorized);
        }

        return boundSteps is null ? plan : plan with { Steps = boundSteps };
    }

    private async Task<AuthorizedEnrichmentLayers> EnsureEnrichmentLayersReadableAsync(
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

        // The same pin for the SOURCE layer. Without it a background submission re-authorized
        // the source against the submitting identity, which for the workflow reconcile tick is
        // the orchestrator principal carrying the wildcard `admin` role — so a source layer no
        // live requester ever authorized (a ForEach placeholder resolves to a concrete id only
        // at run creation) was admitted on admin's authority (honua-server#3043 review).
        var hasRequesterSourceBinding = TryReadLayerId(
            step, EnrichmentJobExecutor.AuthorizedSourceLayerInput, out var requesterAuthorizedSourceLayerId);

        var ambient = _httpContextAccessor.HttpContext;
        if (ambient is null)
        {
            if (hasSourceLayer && (!hasRequesterSourceBinding || requesterAuthorizedSourceLayerId != sourceLayerId))
            {
                // Either no live requester ever authorized a source layer for this step, or the
                // step now names a DIFFERENT one than the requester authorized — the dynamic
                // case. Re-deriving here would authorize it under the background identity, so
                // fail closed exactly as the dataset binding does.
                throw Deny(principal, "background submission carries no requester-authorized source-layer binding");
            }

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

            return new AuthorizedEnrichmentLayers(null, hasSourceLayer ? sourceLayerId : null);
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
        return new AuthorizedEnrichmentLayers(dataset.LayerId, hasSourceLayer ? sourceLayerId : null);
    }

    private readonly record struct AuthorizedEnrichmentLayers(int? DatasetLayerId, int? SourceLayerId);

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

    // Incoming layer pins are replaced by the layer this gate authorized, so a caller cannot supply the binding.
    private static AnalysisPlanStep BindAuthorizedLayers(
        AnalysisPlanStep step,
        AuthorizedEnrichmentLayers authorized)
    {
        var inputs = new Dictionary<string, string>(step.Inputs.Count + 2, StringComparer.Ordinal);
        foreach (var input in step.Inputs)
        {
            if (string.Equals(input.Key, EnrichmentJobExecutor.AuthorizedDatasetLayerInput, StringComparison.Ordinal)
                || string.Equals(input.Key, EnrichmentJobExecutor.AuthorizedSourceLayerInput, StringComparison.Ordinal))
            {
                continue;
            }

            inputs[input.Key] = input.Value;
        }

        if (authorized.DatasetLayerId is { } datasetLayerId)
        {
            inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput] =
                datasetLayerId.ToString(CultureInfo.InvariantCulture);
        }

        if (authorized.SourceLayerId is { } sourceLayerId)
        {
            inputs[EnrichmentJobExecutor.AuthorizedSourceLayerInput] =
                sourceLayerId.ToString(CultureInfo.InvariantCulture);
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
