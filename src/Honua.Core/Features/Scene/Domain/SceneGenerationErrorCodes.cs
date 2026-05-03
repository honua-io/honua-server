// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Stable error codes surfaced by the 3D Tiles generation pipeline (#842).
/// Codes are forwarded through shared problem-detail helpers so clients can
/// branch on them without parsing free-form messages.
/// </summary>
public static class SceneGenerationErrorCodes
{
    /// <summary>The requested layer was not found in the catalog.</summary>
    public const string LayerNotFound = "SCENE_LAYER_NOT_FOUND";

    /// <summary>The layer's spatial reference cannot be resolved or is unsupported for ECEF projection.</summary>
    public const string LayerCrsUnknown = "SCENE_LAYER_CRS_UNKNOWN";

    /// <summary>A feature geometry type is not supported by the v1 generator.</summary>
    public const string UnsupportedGeometryType = "SCENE_UNSUPPORTED_GEOMETRY_TYPE";

    /// <summary>The layer exceeds the configured maximum feature count for v1.</summary>
    public const string FeatureLimitExceeded = "SCENE_FEATURE_LIMIT_EXCEEDED";

    /// <summary>A model asset (glTF/GLB) reference is invalid, missing, or malformed.</summary>
    public const string ModelAssetInvalid = "SCENE_MODEL_ASSET_INVALID";

    /// <summary>An attribute type is not supported by the EXT_structural_metadata writer.</summary>
    public const string AttributeTypeUnsupported = "SCENE_ATTRIBUTE_TYPE_UNSUPPORTED";

    /// <summary>The scene id or display name collides with an existing dataset registration.</summary>
    public const string SceneRegistrationConflict = "SCENE_REGISTRATION_CONFLICT";

    /// <summary>Generation options failed validation (range or shape errors).</summary>
    public const string OptionsInvalid = "SCENE_OPTIONS_INVALID";
}
