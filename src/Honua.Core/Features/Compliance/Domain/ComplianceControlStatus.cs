// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// Status of a single compliance control after automated evidence collection.
/// </summary>
/// <remarks>
/// Values intentionally avoid "Pass" / "Fail" wording — auditors decide passage, the
/// platform only reports whether the technical control is wired up.
/// </remarks>
public enum ComplianceControlStatus
{
    /// <summary>The control's evidence path has not yet been evaluated.</summary>
    Unknown = 0,

    /// <summary>The control is fully implemented and evidence is being collected.</summary>
    Implemented = 1,

    /// <summary>The control is partially implemented — at least one dependency is missing.</summary>
    PartiallyImplemented = 2,

    /// <summary>The control is not implemented; an evidence gap exists.</summary>
    NotImplemented = 3,

    /// <summary>The control does not apply to this deployment (e.g. cloud-only control on self-host).</summary>
    NotApplicable = 4,
}
