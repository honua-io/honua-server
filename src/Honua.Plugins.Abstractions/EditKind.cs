// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// The kind of edit a feature participates in within an edit batch.
/// </summary>
public enum EditKind
{
    /// <summary>A new feature is being created.</summary>
    Create = 0,

    /// <summary>An existing feature is being updated.</summary>
    Update = 1,

    /// <summary>An existing feature is being deleted.</summary>
    Delete = 2,
}
