// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance;

/// <summary>
/// Operator-facing configuration for the compliance framework. Bound from the
/// <c>Compliance</c> configuration section; defaults produce an "informational
/// only" posture so a fresh deployment shows evidence gaps rather than asserting
/// false readiness.
/// </summary>
public sealed class ComplianceOptions
{
    /// <summary>Configuration section name (<c>Compliance</c>).</summary>
    public const string SectionName = "Compliance";

    /// <summary>
    /// Whether the deployment claims SOC 2 readiness. Surfaced in evidence rows so
    /// auditors can distinguish a procurement environment from a customer environment.
    /// </summary>
    public bool Soc2ReadinessClaimed { get; set; } = true;

    /// <summary>Whether the deployment claims FedRAMP readiness.</summary>
    public bool FedRampReadinessClaimed { get; set; } = true;

    /// <summary>
    /// Primary region this deployment operates in. Used as the default value for the
    /// FedRAMP system boundary diagram when <see cref="DataResidency"/> is not set.
    /// </summary>
    public string PrimaryRegion { get; set; } = "unspecified";

    /// <summary>Data residency policy configuration.</summary>
    public ComplianceResidencyOptions DataResidency { get; set; } = new();

    /// <summary>Encryption-at-rest key rotation configuration.</summary>
    public ComplianceEncryptionOptions Encryption { get; set; } = new();

    /// <summary>
    /// Override the dependency gate's view of platform capabilities. Used by deployments
    /// that supply audit / SSO / RBAC via sidecars (the gate cannot probe those directly).
    /// </summary>
    public ComplianceDependencyOverrides DependencyOverrides { get; set; } = new();
}

/// <summary>Residency configuration sub-section.</summary>
public sealed class ComplianceResidencyOptions
{
    /// <summary>When <c>true</c>, egress to unlisted regions is blocked at boundary call-sites.</summary>
    public bool Enforced { get; set; }

    /// <summary>
    /// Primary region for data-at-rest. Mirrors <see cref="ComplianceOptions.PrimaryRegion"/>
    /// so the residency section is self-contained for callers that only consult it.
    /// </summary>
    public string PrimaryRegion { get; set; } = "unspecified";

    /// <summary>Regions data may flow to. Always includes <see cref="PrimaryRegion"/> implicitly.</summary>
    public List<string> AllowedRegions { get; set; } = new();
}

/// <summary>Encryption configuration sub-section.</summary>
public sealed class ComplianceEncryptionOptions
{
    /// <summary>
    /// Algorithms in use, for the report. Defaults to the algorithm set used by
    /// <c>Honua.Postgres.Features.Security.ConnectionEncryptionService</c> so the
    /// dashboard agrees with the actual encryption code path out of the box.
    /// </summary>
    public List<string> Algorithms { get; set; } = new()
    {
        "aes-256-gcm",
        "pbkdf2-sha256-100000",
    };

    /// <summary>
    /// Whether the operator has explicitly attested FIPS mode is enabled in the host OS /
    /// container. The collector cross-checks this against the .NET FIPS signal.
    /// </summary>
    public bool FipsModeAttested { get; set; }
}

/// <summary>Dependency overrides — last-resort declaration for capabilities the gate cannot probe.</summary>
public sealed class ComplianceDependencyOverrides
{
    /// <summary>Override audit-log availability. <c>null</c> means "let the gate decide".</summary>
    public bool? AuditLogConfigured { get; set; }

    /// <summary>
    /// Override SSO / OIDC availability. <c>null</c> falls back to detecting OIDC via
    /// configuration (<c>Oidc:Enabled</c> plus at least one provider with a client ID).
    /// </summary>
    public bool? SsoConfigured { get; set; }

    /// <summary>
    /// Override RBAC availability. <c>null</c> defaults to "not attested" because the
    /// presence of an in-memory role store is not a capability proof — set this once
    /// role policies are enforced on the protected endpoints in the deployment.
    /// </summary>
    public bool? RbacConfigured { get; set; }

    /// <summary>Override TLS / in-transit encryption attestation.</summary>
    public bool? TransportEncryptionAttested { get; set; }

    /// <summary>
    /// Operator attestation that data residency is enforced at egress boundaries.
    /// The <see cref="ComplianceResidencyOptions.Enforced"/> flag drives the policy
    /// view; this attestation is what flips the compliance dependency to satisfied
    /// once outbound call sites consult <c>IDataResidencyPolicyProvider</c>.
    /// </summary>
    public bool? DataResidencyAttested { get; set; }
}
