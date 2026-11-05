// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public sealed record SceneExtent(
    double XMin,
    double YMin,
    double XMax,
    double YMax,
    double? ZMin = null,
    double? ZMax = null);
