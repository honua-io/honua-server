// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// WGS-84 axis-aligned bounding box for a scene dataset, in decimal degrees.
/// </summary>
/// <param name="XMin">Minimum longitude (-180 to 180).</param>
/// <param name="YMin">Minimum latitude (-90 to 90).</param>
/// <param name="XMax">Maximum longitude (-180 to 180).</param>
/// <param name="YMax">Maximum latitude (-90 to 90).</param>
public sealed record SceneExtent(double XMin, double YMin, double XMax, double YMax);
