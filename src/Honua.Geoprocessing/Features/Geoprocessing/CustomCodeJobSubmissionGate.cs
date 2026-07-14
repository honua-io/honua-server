// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing.CustomCode;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Consolidates the custom-code submit gate for <see cref="GeoprocessingJobService"/>:
/// the scoped job-bound token issuance + injection (via <see cref="CustomCodeSubmitCoordinator"/>),
/// the commit-signature verifier, and the custom-code policy options. Owns the
/// <see cref="IScopedJobTokenIssuer"/> and <see cref="ICustomCodeCommitSignatureVerifier"/>
/// collaborators so the job service no longer threads them (and the coordinator it builds
/// from them) through its own constructor. Behavior, logging, and the surfaced
/// <see cref="GeoprocessingValidationException"/> mapping are identical to the inline logic
/// the service previously performed.
/// </summary>
internal sealed class CustomCodeJobSubmissionGate
{
    private readonly ILogger<GeoprocessingJobService> _logger;
    private readonly IScopedJobTokenIssuer? _scopedJobTokenIssuer;
    private readonly IOptionsMonitor<CustomCodeOptions>? _customCodeOptions;
    private readonly CustomCodeSubmitCoordinator? _coordinator;

    /// <summary>
    /// Creates the custom-code submit gate. When no scoped-job token issuer is configured the
    /// gate is dormant and every custom-code submission fails closed with a validation error.
    /// </summary>
    public CustomCodeJobSubmissionGate(
        ILogger<GeoprocessingJobService> logger,
        IScopedJobTokenIssuer? scopedJobTokenIssuer = null,
        IOptionsMonitor<CustomCodeOptions>? customCodeOptions = null,
        ICustomCodeCommitSignatureVerifier? customCodeSignatureVerifier = null)
    {
        _logger = logger;
        _scopedJobTokenIssuer = scopedJobTokenIssuer;
        _customCodeOptions = customCodeOptions;
        _coordinator = scopedJobTokenIssuer is null
            ? null
            : new CustomCodeSubmitCoordinator(scopedJobTokenIssuer, customCodeSignatureVerifier);
    }

    /// <summary>
    /// Runs the custom-code submit gate: validates the <c>customcode.*</c> parameters, clamps
    /// the declared scope to ⊆ the submitter, mints the scoped job-bound token, and injects it
    /// (plus the API base URL and the server-set output prefix) into <paramref name="specParams"/>.
    /// Throws <see cref="GeoprocessingValidationException"/> on rejection so the adapter maps it
    /// onto the same validation channel ordinary plan failures use.
    /// </summary>
    public async Task<(CustomCodeOwnerScope OwnerScope, string Token)> ValidateMintAndInjectAsync(
        string jobId,
        ClaimsPrincipal principal,
        Dictionary<string, string> specParams,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null || _customCodeOptions is null)
        {
            throw new GeoprocessingValidationException(
                "Custom-code geoprocessing is not enabled on this server (no scoped-job token issuer is configured).");
        }

        // Open-policy gate (PA-196): an unrestricted-repo execution surface requires admin
        // elevation. A non-admin caller is rejected even when Open is configured so that
        // arbitrary code execution from any HTTPS repo is never reachable by a normal user.
        if (_customCodeOptions.CurrentValue.RepoPolicy == CustomCodeRepoPolicy.Open
            && !principal.IsInRole("admin"))
        {
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        }

        try
        {
            var result = await _coordinator.ValidateMintAndInjectAsync(
                jobId, principal, specParams, _customCodeOptions.CurrentValue, cancellationToken).ConfigureAwait(false);
            return (result.OwnerScope, result.Token);
        }
        catch (CustomCodeSubmitRejectedException ex)
        {
            GeoprocessingServiceLog.DeclaredScopeRejected(_logger, ex.Message);
            throw new GeoprocessingValidationException(ex.Message);
        }
    }

    /// <summary>
    /// Best-effort revocation of a scoped callback token so a credential is never left valid
    /// for a job whose submission rolled back (the token must not outlive the job — Phase-0
    /// invariant #5). No-ops when the token is absent or the issuer is not configured.
    /// </summary>
    public async Task TryRevokeTokenAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || _scopedJobTokenIssuer is null)
        {
            return;
        }

        try
        {
            await _scopedJobTokenIssuer.RevokeAsync(token, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Intentionally broad: this is the documented best-effort revocation path (see
            // the XML doc above) — a revoke failure must not fail job submission/cleanup, but
            // it is logged so an un-revoked token is diagnosable rather than silently dropped.
            GeoprocessingServiceLog.CustomCodeTokenRevokeFailed(_logger, ex);
        }
    }
}
