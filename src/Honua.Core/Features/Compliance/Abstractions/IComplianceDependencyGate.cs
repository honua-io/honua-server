// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Probes the host for the upstream platform capabilities a compliance control
/// depends on (audit log, SSO/OIDC, RBAC, encryption posture). The dependency-gate
/// design exists so that the evidence collector never claims a control is
/// "Implemented" when its prerequisites are missing — which is the
/// "dependencies are explicit and enforced" acceptance criterion from #352.
/// </summary>
public interface IComplianceDependencyGate
{
    /// <summary>Returns <c>true</c> if the supplied dependency is operational in this deployment.</summary>
    bool IsSatisfied(ComplianceDependency dependency);

    /// <summary>
    /// Returns a short, sanitized reason for the dependency state — populated on both
    /// satisfied and unsatisfied paths so audit reports can quote it verbatim.
    /// </summary>
    string DescribeStatus(ComplianceDependency dependency);
}
