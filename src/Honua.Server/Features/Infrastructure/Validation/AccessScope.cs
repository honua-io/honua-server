// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Defines the scope of access required for a layer or service operation.
/// </summary>
public enum AccessScope
{
    /// <summary>
    /// Read-only access is required.
    /// </summary>
    Read = 0,

    /// <summary>
    /// Write access is required (includes read permissions).
    /// </summary>
    Write = 1
}