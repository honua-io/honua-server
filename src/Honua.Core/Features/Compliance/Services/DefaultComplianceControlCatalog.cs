// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// In-memory compliance control catalog. Seeds the SOC 2 / FedRAMP controls the server's
/// automated evidence collector understands today — adding controls here is intentionally
/// the only step needed to surface them on the dashboard (no schema migration).
/// </summary>
/// <remarks>
/// Controls are kept deliberately minimal — only the subset that has automated evidence
/// is published. Auditor-required controls without server-side automation belong in the
/// system security plan, not in the dashboard, to avoid implying "Implemented" for
/// process controls the server cannot substantiate.
/// </remarks>
internal sealed class DefaultComplianceControlCatalog : IComplianceControlCatalog
{
    private readonly FrozenDictionary<string, ComplianceControl> _byId;

    public DefaultComplianceControlCatalog()
    {
        Controls = BuildCatalog();
        _byId = Controls.ToFrozenDictionary(c => c.ControlId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ComplianceControl> Controls { get; }

    public ComplianceControl? GetControl(string controlId)
    {
        if (string.IsNullOrWhiteSpace(controlId))
        {
            return null;
        }

        return _byId.TryGetValue(controlId, out var control) ? control : null;
    }

    private static ComplianceControl[] BuildCatalog()
    {
        return
        [
            // SOC 2 Trust Services Criteria
            new ComplianceControl
            {
                ControlId = "soc2.cc6.1",
                Framework = ComplianceFramework.Soc2,
                Title = "Logical access controls",
                Description = "The entity restricts logical access to information assets through identification, authentication, and authorization.",
                Dependencies = [ComplianceDependency.Sso, ComplianceDependency.Rbac, ComplianceDependency.AuditLog],
                RelatedControls = ["fedramp.ac-2", "fedramp.ia-2"],
            },
            new ComplianceControl
            {
                ControlId = "soc2.cc6.6",
                Framework = ComplianceFramework.Soc2,
                Title = "Transmission and disposal controls",
                Description = "The entity implements logical access security measures to protect against threats from sources outside its system boundaries.",
                Dependencies = [ComplianceDependency.EncryptionInTransit],
                RelatedControls = ["fedramp.sc-8"],
            },
            new ComplianceControl
            {
                ControlId = "soc2.cc6.7",
                Framework = ComplianceFramework.Soc2,
                Title = "Restriction and protection of data at rest",
                Description = "The entity restricts the transmission, movement, and removal of information to authorized internal and external users.",
                Dependencies = [ComplianceDependency.EncryptionAtRest, ComplianceDependency.DataResidency],
                RelatedControls = ["fedramp.sc-28"],
            },
            new ComplianceControl
            {
                ControlId = "soc2.cc7.2",
                Framework = ComplianceFramework.Soc2,
                Title = "Detection and monitoring",
                Description = "The entity monitors system components and the operation of those components for anomalies indicative of malicious acts, natural disasters, and errors affecting the entity's ability to meet its objectives.",
                Dependencies = [ComplianceDependency.AuditLog],
                RelatedControls = ["fedramp.au-2", "fedramp.au-12"],
            },
            new ComplianceControl
            {
                ControlId = "soc2.cc7.3",
                Framework = ComplianceFramework.Soc2,
                Title = "Evaluation of security events",
                Description = "The entity evaluates security events to determine whether they could or have resulted in a failure of the entity to meet its objectives (security incidents).",
                Dependencies = [ComplianceDependency.AuditLog],
                RelatedControls = ["fedramp.ir-4"],
            },

            // FedRAMP Moderate baseline (NIST SP 800-53 controls)
            new ComplianceControl
            {
                ControlId = "fedramp.ac-2",
                Framework = ComplianceFramework.FedRamp,
                Title = "Account management",
                Description = "The organization manages information system accounts including identification, authorization, monitoring, and removal of accounts.",
                Dependencies = [ComplianceDependency.Sso, ComplianceDependency.Rbac, ComplianceDependency.AuditLog],
                RelatedControls = ["soc2.cc6.1"],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.au-2",
                Framework = ComplianceFramework.FedRamp,
                Title = "Audit events",
                Description = "The organization determines that the information system is capable of auditing events and records sufficient detail for forensic investigation.",
                Dependencies = [ComplianceDependency.AuditLog],
                RelatedControls = ["soc2.cc7.2"],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.au-12",
                Framework = ComplianceFramework.FedRamp,
                Title = "Audit generation",
                Description = "The information system provides audit record generation capability for the auditable events defined in AC-2 and AU-2.",
                Dependencies = [ComplianceDependency.AuditLog],
                RelatedControls = ["soc2.cc7.2"],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.ia-2",
                Framework = ComplianceFramework.FedRamp,
                Title = "Identification and authentication",
                Description = "The information system uniquely identifies and authenticates organizational users.",
                Dependencies = [ComplianceDependency.Sso],
                RelatedControls = ["soc2.cc6.1"],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.sc-8",
                Framework = ComplianceFramework.FedRamp,
                Title = "Transmission confidentiality and integrity",
                Description = "The information system protects the confidentiality and integrity of transmitted information using FIPS-validated cryptography.",
                Dependencies = [ComplianceDependency.EncryptionInTransit],
                RelatedControls = ["soc2.cc6.6"],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.sc-13",
                Framework = ComplianceFramework.FedRamp,
                Title = "Cryptographic protection",
                Description = "The information system implements FIPS-validated or NSA-approved cryptography.",
                Dependencies = [ComplianceDependency.EncryptionAtRest, ComplianceDependency.EncryptionInTransit],
                RelatedControls = [],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.sc-28",
                Framework = ComplianceFramework.FedRamp,
                Title = "Protection of information at rest",
                Description = "The information system protects the confidentiality and integrity of information at rest with FIPS-validated cryptography.",
                Dependencies = [ComplianceDependency.EncryptionAtRest],
                RelatedControls = ["soc2.cc6.7"],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.sc-7",
                Framework = ComplianceFramework.FedRamp,
                Title = "Boundary protection",
                Description = "The information system monitors and controls communications at the external boundary of the system and at key internal boundaries.",
                Dependencies = [ComplianceDependency.DataResidency, ComplianceDependency.EncryptionInTransit],
                RelatedControls = ["soc2.cc6.7"],
            },
            new ComplianceControl
            {
                ControlId = "fedramp.ir-4",
                Framework = ComplianceFramework.FedRamp,
                Title = "Incident handling",
                Description = "The organization implements an incident-handling capability that includes preparation, detection, analysis, containment, eradication, and recovery.",
                Dependencies = [ComplianceDependency.AuditLog],
                RelatedControls = ["soc2.cc7.3"],
            },
        ];
    }
}
