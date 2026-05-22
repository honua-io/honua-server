// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// Compliance frameworks tracked by the Honua server.
/// </summary>
/// <remarks>
/// <para>
/// The platform reports <i>readiness</i> evidence — it does not assert authorization.
/// Authorization (SOC 2 Type II report, FedRAMP Moderate ATO, etc.) is granted by a
/// qualified auditor or agency, not by the server. Per scope decisions for ticket #352
/// the server only surfaces the technical control posture that those audits will inspect.
/// </para>
/// </remarks>
public enum ComplianceFramework
{
    /// <summary>SOC 2 (Trust Services Criteria) readiness evidence.</summary>
    Soc2 = 0,

    /// <summary>FedRAMP (Moderate baseline) readiness evidence.</summary>
    FedRamp = 1,
}
