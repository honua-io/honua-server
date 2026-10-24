// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Outcome of an audited action.
/// </summary>
public enum AuditOutcome
{
    /// <summary>Action completed successfully.</summary>
    Success = 0,

    /// <summary>Action failed due to error (validation, internal failure, etc.).</summary>
    Failure = 1,

    /// <summary>Action was rejected by authorization (forbidden / unauthorized).</summary>
    Denied = 2,
}
