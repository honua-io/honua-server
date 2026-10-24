// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Validation;

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
