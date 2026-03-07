// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Sdk.Models;

/// <summary>
/// Audit logging levels for administrative operations.
/// </summary>
public enum AuditLevel
{
    /// <summary>
    /// No audit logging.
    /// </summary>
    None = 0,

    /// <summary>
    /// Basic audit logging for major operations.
    /// </summary>
    Basic = 1,

    /// <summary>
    /// Standard audit logging for most operations.
    /// </summary>
    Standard = 2,

    /// <summary>
    /// Detailed audit logging including all field changes.
    /// </summary>
    Detailed = 3,

    /// <summary>
    /// Full audit logging including all operations and system events.
    /// </summary>
    Full = 4
}