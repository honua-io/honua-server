// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>
/// The repository-allowlist policy mode applied to a custom-code job's
/// <c>customcode.repo_url</c> at submit time.
/// </summary>
public enum CustomCodeRepoPolicy
{
    /// <summary>
    /// Reject every repository — custom-code submission is disabled. This is the
    /// fail-closed default when the operator has not opted in by configuring an
    /// allowlist (or explicitly choosing <see cref="Open"/>).
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Only repositories whose host is on <see cref="CustomCodeOptions.RepoAllowlist"/>
    /// may be submitted. This is the recommended posture.
    /// </summary>
    OrgAllowlist = 1,

    /// <summary>
    /// Any HTTPS repository is accepted. Intended for trusted single-tenant
    /// deployments only — never combine with untrusted submitters.
    /// </summary>
    Open = 2,

    /// <summary>
    /// The strongest posture (Phase 3 supply-chain hardening). The repository must
    /// satisfy the org-allowlist <em>and</em> the pinned commit must carry a verified
    /// signature (GPG/sigstore) by a trusted key. A commit whose signature is absent,
    /// invalid, by an untrusted key, or simply <em>unverifiable</em> (the configured
    /// verifier cannot reach the provider) is rejected — the gate fails closed. The
    /// per-tenant allowlist (when configured) is enforced in addition.
    /// </summary>
    SignedOnly = 3,
}

/// <summary>
/// Configuration for the custom-code geoprocessing submit path: the repository
/// allowlist policy, the Honua API base URL injected into user-code containers,
/// the server-set S3 output-prefix root, and the scoped-token lifetime knobs.
/// </summary>
internal sealed class CustomCodeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Geoprocessing:CustomCode";

    /// <summary>
    /// Repository-allowlist policy. Defaults to
    /// <see cref="CustomCodeRepoPolicy.OrgAllowlist"/> — the org-allowlist posture
    /// the contract specifies as the default; with an empty
    /// <see cref="RepoAllowlist"/> this rejects every repository (fail-closed). The
    /// MVP default is unchanged; <see cref="CustomCodeRepoPolicy.SignedOnly"/> is the
    /// opt-in Phase 3 supply-chain posture.
    /// </summary>
    public CustomCodeRepoPolicy RepoPolicy { get; set; } = CustomCodeRepoPolicy.OrgAllowlist;

    /// <summary>
    /// Allowed repository hosts (e.g. <c>github.com</c>,
    /// <c>git.internal.example</c>) honored when <see cref="RepoPolicy"/> is
    /// <see cref="CustomCodeRepoPolicy.OrgAllowlist"/> or
    /// <see cref="CustomCodeRepoPolicy.SignedOnly"/>. Matched case-insensitively
    /// against the URL host; an entry of the form <c>host/org</c> additionally
    /// constrains the first path segment.
    /// </summary>
    public List<string> RepoAllowlist { get; set; } = [];

    /// <summary>
    /// Optional per-tenant repository allowlist (Phase 3). Keyed by the submitting
    /// principal's <c>tenant_id</c>; the value is a list of allowlist entries in the
    /// same <c>host</c> or <c>host/org</c> grammar as <see cref="RepoAllowlist"/>.
    /// When a tenant has an entry here, the submitted repo_url must match the
    /// tenant's list <em>in addition to</em> the org-wide allowlist — both gates must
    /// pass, so a tenant list can only narrow what the org allows, never widen it.
    /// A tenant absent from this map is unconstrained by the per-tenant gate and
    /// falls through to the org allowlist alone (behavior-preserving default).
    /// Enforced under <see cref="CustomCodeRepoPolicy.OrgAllowlist"/> and
    /// <see cref="CustomCodeRepoPolicy.SignedOnly"/>.
    /// </summary>
    public Dictionary<string, List<string>> TenantRepoAllowlist { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Trusted signing-key identifiers (e.g. GPG long key-ids / fingerprints, or
    /// sigstore identities) accepted when <see cref="RepoPolicy"/> is
    /// <see cref="CustomCodeRepoPolicy.SignedOnly"/>. A verified commit signature
    /// whose signer is not on this list is rejected. When empty under
    /// <see cref="CustomCodeRepoPolicy.SignedOnly"/> the gate fails closed (no key is
    /// trusted, so every submission is rejected) — configure at least one key to use
    /// signed-only.
    /// </summary>
    public List<string> TrustedSignerKeys { get; set; } = [];

    /// <summary>
    /// The Honua API endpoint injected into the container as <c>HONUA_BASE_URL</c>
    /// so the user code's SDK callbacks reach this server. Required for custom-code
    /// submission to a remote Batch workload; must be an absolute HTTPS URL.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// The S3 URI prefix root under which each job's server-set
    /// <c>customcode.output_prefix</c> is allocated (e.g.
    /// <c>s3://honua-customcode/outputs</c>). The per-job prefix is this root plus
    /// the job id.
    /// </summary>
    public string OutputPrefixRoot { get; set; } = string.Empty;

    /// <summary>
    /// Maximum wall-clock execution time allowed for a custom-code job, used to
    /// bound the scoped token's lifetime when the workload declares no explicit
    /// <c>batch.timeout_seconds</c>.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00", ErrorMessage = "JobTimeout must be between 1 minute and 24 hours")]
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Grace period added to the job timeout when computing the scoped token's
    /// absolute expiry, so the callback credential outlives in-flight requests at
    /// the moment the job is timing out but never long after.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00", ErrorMessage = "TokenGrace must be between 30 seconds and 1 hour")]
    public TimeSpan TokenGrace { get; set; } = TimeSpan.FromMinutes(5);
}
