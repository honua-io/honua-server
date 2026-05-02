// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Lifecycle state of a scene dataset registry entry.
/// </summary>
public enum SceneDatasetStatus
{
    /// <summary>
    /// Dataset is registered, validated, and resolvable for hosted serving.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Dataset has been soft-deactivated and is hidden from hosted serving.
    /// </summary>
    Inactive = 1,

    /// <summary>
    /// Dataset failed validation and is not eligible for serving.
    /// </summary>
    ValidationFailed = 2
}
