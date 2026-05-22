// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// A compliance control definition — the auditor-facing identifier and description
/// that the server's evidence collection is mapped to.
/// </summary>
/// <remarks>
/// Controls are static metadata sourced from <see cref="Abstractions.IComplianceControlCatalog"/>.
/// Their status / evidence is collected at runtime.
/// </remarks>
public sealed record ComplianceControl
{
    /// <summary>
    /// Stable, framework-prefixed control identifier (e.g. <c>soc2.cc6.1</c>, <c>fedramp.ac-2</c>).
    /// Used as the join key between the catalog and the evidence collector.
    /// </summary>
    public required string ControlId { get; init; }

    /// <summary>Compliance framework the control belongs to.</summary>
    public required ComplianceFramework Framework { get; init; }

    /// <summary>Short, auditor-recognizable title (e.g. <c>Logical Access Controls</c>).</summary>
    public required string Title { get; init; }

    /// <summary>One-paragraph description of what the control requires.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Server dependencies (audit log, RBAC, SSO) that must be operational before the
    /// control can claim "Implemented". Empty if no dependencies.
    /// </summary>
    public IReadOnlyList<ComplianceDependency> Dependencies { get; init; } = Array.Empty<ComplianceDependency>();

    /// <summary>
    /// Cross-references to related controls in other frameworks (e.g. SOC 2 CC6.1 maps to
    /// FedRAMP AC-2). Free-text — not validated against the catalog so additions don't break.
    /// </summary>
    public IReadOnlyList<string> RelatedControls { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A platform capability a compliance control depends on. Used to surface evidence gaps
/// (e.g. "RBAC is not enabled, so authorization controls cannot be substantiated").
/// </summary>
public enum ComplianceDependency
{
    /// <summary>Audit log persistence (#350).</summary>
    AuditLog = 0,

    /// <summary>SSO / OIDC identity (#348).</summary>
    Sso = 1,

    /// <summary>Role-based access control (#349).</summary>
    Rbac = 2,

    /// <summary>Encryption-at-rest for stored secrets.</summary>
    EncryptionAtRest = 3,

    /// <summary>Encryption-in-transit (TLS) for all callers.</summary>
    EncryptionInTransit = 4,

    /// <summary>Data residency policy enforcement.</summary>
    DataResidency = 5,
}
