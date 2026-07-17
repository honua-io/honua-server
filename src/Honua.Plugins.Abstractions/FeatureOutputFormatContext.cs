// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Read-only context supplied to an <see cref="IFeatureOutputFormat"/> when serializing a stream of
/// features (issue #2856). Carries the layer/service identity, the projected attribute schema, and
/// the output spatial reference so a plugin format can write a header, resolve attribute values, and
/// annotate coordinate system without coupling to the host's protocol or metadata internals.
/// </summary>
/// <param name="ServiceId">The service the exported layer belongs to.</param>
/// <param name="LayerId">The published layer id being exported.</param>
/// <param name="ResourceName">Human-readable layer/resource name used for filenames and labels.</param>
/// <param name="Fields">The ordered attribute columns to project, matching the caller's field selection.</param>
/// <param name="OutputSrid">The spatial reference id of the geometry the writer receives (WKB in <c>Feature.Geometry</c>).</param>
public sealed record FeatureOutputFormatContext(
    string ServiceId,
    int LayerId,
    string ResourceName,
    ImmutableArray<FeatureOutputField> Fields,
    int OutputSrid);
