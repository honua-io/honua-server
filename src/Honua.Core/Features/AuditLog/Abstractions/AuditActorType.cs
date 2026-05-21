// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Classifies the principal that performed the audited action.
/// </summary>
public enum AuditActorType
{
    /// <summary>Caller is unauthenticated / anonymous.</summary>
    Anonymous = 0,

    /// <summary>Caller is a human user identified by user id (OIDC / admin session).</summary>
    UserId = 1,

    /// <summary>Caller authenticated using an API key (identified by hashed key id).</summary>
    ApiKey = 2,

    /// <summary>Caller is the server itself / a background service.</summary>
    System = 3,
}
