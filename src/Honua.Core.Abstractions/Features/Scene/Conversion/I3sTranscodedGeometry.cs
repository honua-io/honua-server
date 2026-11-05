// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Conversion;

public readonly record struct I3sTranscodedGeometry(
    byte[] Buffer,
    IReadOnlyList<double> MbsCenterEcef,
    double MbsRadiusMeters,
    int VertexCount,
    int FeatureCount);
