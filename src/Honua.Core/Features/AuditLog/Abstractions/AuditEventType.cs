// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// High-level category of an audit event. The set of values is intentionally
/// small and stable so downstream forensic / compliance tooling can filter on
/// a finite, well-known vocabulary. Free-form detail belongs in
/// <see cref="AuditEvent.Action"/> and <see cref="AuditEvent.Details"/>.
/// </summary>
public enum AuditEventType
{
    /// <summary>Authentication of a caller (API key, session, OIDC, etc.).</summary>
    Authentication = 0,

    /// <summary>Authorization / access-control decision after authentication.</summary>
    Authorization = 1,

    /// <summary>Administrative action invoked against the control plane.</summary>
    AdminAction = 2,

    /// <summary>Mutation of server configuration, schema, or policies.</summary>
    ConfigChange = 3,

    /// <summary>Data export — bulk read or download of customer data.</summary>
    DataExport = 4,

    /// <summary>Destructive data operation — deletes, drops, purges.</summary>
    DataDelete = 5,
}
