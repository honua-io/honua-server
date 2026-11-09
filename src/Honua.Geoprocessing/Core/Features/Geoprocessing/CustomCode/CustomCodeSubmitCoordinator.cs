// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Geoprocessing.CustomCode;

internal sealed class CustomCodeSubmitCoordinator(
    IScopedJobTokenIssuer tokenIssuer,
    ICustomCodeCommitSignatureVerifier? signatureVerifier = null)
{
    private readonly IScopedJobTokenIssuer _tokenIssuer = tokenIssuer
        ?? throw new ArgumentNullException(nameof(tokenIssuer));

    // The commit-signature verifier consulted only under the signed-only posture.
    // Defaults to the fail-closed verifier so signed-only without a configured
    // verifier rejects every submission rather than admitting unsigned code.
    private readonly ICustomCodeCommitSignatureVerifier _signatureVerifier =
        signatureVerifier ?? UnverifiableCommitSignatureVerifier.Instance;

    private const string PermissionClaimType = "permission";

    private const string TenantClaimType = "tenant_id";

    public async Task<CustomCodeSubmitResult> ValidateMintAndInjectAsync(
        string jobId,
        ClaimsPrincipal principal,
        Dictionary<string, string> specParams,
        CustomCodeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(specParams);
        ArgumentNullException.ThrowIfNull(options);

        var tenantId = principal.FindFirstValue(TenantClaimType);

        if (!CustomCodeSubmitValidator.TryValidate(
                specParams,
                options,
                tenantId,
                out var declaredScope,
                out var repoUri,
                out var commitSha,
                out var requiresSignatureVerification,
                out var paramRejection))
        {
            throw new CustomCodeSubmitRejectedException(paramRejection!);
        }

        // Signed-only posture: the pinned commit must carry a valid signature by a
        // trusted key. Run the verification seam and fail closed on anything that is
        // not a verified, trusted signature. This is the supply-chain (T4) gate.
        if (requiresSignatureVerification)
        {
            await EnsureCommitTrustedAsync(repoUri!, commitSha!, options, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(options.ApiBaseUrl) ||
            !Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomCodeSubmitRejectedException(
                "Custom-code submission requires a configured absolute https Geoprocessing:CustomCode:ApiBaseUrl.");
        }

        // Snapshot the submitter's reach from the LIVE principal and reject any
        // declared-scope entry the submitter cannot actually reach. The pinned scope
        // is therefore always ⊆ submitter; the token mint can only attenuate it.
        var roles = SnapshotRoles(principal);
        var grants = SnapshotGrants(principal);
        if (!ScopedJobAttenuation.IsWithinOwner(roles, grants, globalDataEditorRoles: null, declaredScope, out var unreachable))
        {
            var target = unreachable is null
                ? "(unspecified)"
                : unreachable.LayerId is null
                    ? unreachable.ServiceId
                    : string.Concat(unreachable.ServiceId, "/", unreachable.LayerId);
            throw new CustomCodeSubmitRejectedException(
                $"Declared scope entry '{target}' exceeds the submitter's permissions and cannot be granted to custom code.");
        }

        var principalId = principal.Identity?.Name ?? string.Empty;
        var ownerScope = new CustomCodeOwnerScope(principalId, tenantId, roles, grants, declaredScope);

        // Server-set the per-job output prefix; never honor a caller-supplied value
        // so user code cannot redirect its outputs outside its isolated prefix.
        specParams[CustomCodeJobContract.OutputPrefixParam] = BuildOutputPrefix(options.OutputPrefixRoot, jobId);

        // Mint the scoped, job-bound token. ResourceScope = the validated declared
        // scope (⊆ owner); the issuer re-intersects with the owner snapshot so the
        // effective grant can only narrow.
        var expiresAt = DateTimeOffset.UtcNow + ResolveJobTimeout(specParams, options) + options.TokenGrace;
        var issuance = await _tokenIssuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: principalId,
                TenantId: tenantId,
                Roles: roles,
                Grants: grants,
                JobId: jobId,
                ResourceScope: declaredScope,
                ExpiresAt: expiresAt),
            cancellationToken).ConfigureAwait(false);

        // Inject the token + base URL via the env.* pass-through.
        specParams[CustomCodeJobContract.BaseUrlEnvParam] = options.ApiBaseUrl;
        specParams[CustomCodeJobContract.JobTokenEnvParam] = issuance.Token;

        // Project the customcode.* parameters to the env.CUSTOMCODE_* pass-through the
        // user-code harness reads (docker/worker-customcode-python/harness/jobspec.py).
        // Batch backends strip the env. prefix and surface each as the matching
        // CUSTOMCODE_* container env var. This is the
        // SERVER half of the cross-piece param->env contract (#2191); only project
        // keys that are actually present so optional parameters stay unset. The
        // Round-4 harness drift guard pins the same map via ParameterToEnv.
        foreach (var (paramKey, envName) in CustomCodeJobContract.ParameterToEnvName)
        {
            if (specParams.TryGetValue(paramKey, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                specParams[CustomCodeJobContract.ToEnvParamKey(envName)] = value;
            }
        }

        return new CustomCodeSubmitResult(ownerScope, issuance.Token);
    }

    private async Task EnsureCommitTrustedAsync(
        Uri repoUri,
        string commitSha,
        CustomCodeOptions options,
        CancellationToken cancellationToken)
    {
        // No trusted key configured ⇒ nothing can be trusted ⇒ reject (fail closed).
        if (options.TrustedSignerKeys is not { Count: > 0 })
        {
            throw new CustomCodeSubmitRejectedException(
                "Custom-code signed-only policy is enabled but no trusted signer keys are configured; every submission is rejected.");
        }

        var result = await _signatureVerifier
            .VerifyAsync(repoUri, commitSha, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSignatureValid)
        {
            var detail = string.IsNullOrWhiteSpace(result.Detail) ? "the commit signature is missing or invalid" : result.Detail;
            throw new CustomCodeSubmitRejectedException(
                $"Custom-code commit '{commitSha}' was rejected by the signed-only policy: {detail}.");
        }

        if (string.IsNullOrWhiteSpace(result.SignerKeyId) ||
            !options.TrustedSignerKeys.Any(k => string.Equals(k, result.SignerKeyId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CustomCodeSubmitRejectedException(
                $"Custom-code commit '{commitSha}' is signed by a key that is not on the configured trusted-signer list.");
        }
    }

    private static TimeSpan ResolveJobTimeout(Dictionary<string, string> specParams, CustomCodeOptions options)
    {
        // Prefer the workload's declared Batch timeout when present so the token's
        // lifetime tracks the actual job ceiling; otherwise fall back to the option.
        if (specParams.TryGetValue("batch.timeout_seconds", out var raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return options.JobTimeout;
    }

    private static string BuildOutputPrefix(string root, string jobId)
    {
        var trimmedRoot = string.IsNullOrWhiteSpace(root) ? "customcode/outputs" : root.TrimEnd('/');
        return string.Concat(trimmedRoot, "/", jobId);
    }

    private static List<string> SnapshotRoles(ClaimsPrincipal principal)
    {
        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        foreach (var value in principal.FindAll("roles")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !roles.Contains(value)))
        {
            roles.Add(value);
        }

        return roles;
    }

    private static List<string> SnapshotGrants(ClaimsPrincipal principal) =>
        principal.FindAll(PermissionClaimType)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
}

internal sealed record CustomCodeSubmitResult(CustomCodeOwnerScope OwnerScope, string Token);

internal sealed class CustomCodeSubmitRejectedException(string message) : Exception(message);
