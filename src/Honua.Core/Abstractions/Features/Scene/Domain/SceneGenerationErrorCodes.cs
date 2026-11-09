// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public static class SceneGenerationErrorCodes
{
    public const string LayerNotFound = "SCENE_LAYER_NOT_FOUND";

    public const string LayerCrsUnknown = "SCENE_LAYER_CRS_UNKNOWN";

    public const string UnsupportedGeometryType = "SCENE_UNSUPPORTED_GEOMETRY_TYPE";

    public const string FeatureLimitExceeded = "SCENE_FEATURE_LIMIT_EXCEEDED";

    public const string ModelAssetInvalid = "SCENE_MODEL_ASSET_INVALID";

    public const string AttributeTypeUnsupported = "SCENE_ATTRIBUTE_TYPE_UNSUPPORTED";

    public const string SceneRegistrationConflict = "SCENE_REGISTRATION_CONFLICT";

    public const string OptionsInvalid = "SCENE_OPTIONS_INVALID";
}
