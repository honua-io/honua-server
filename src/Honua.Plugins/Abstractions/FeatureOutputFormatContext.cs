// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Plugins.Abstractions;

public sealed record FeatureOutputFormatContext(
    string ServiceId,
    int LayerId,
    string ResourceName,
    ImmutableArray<FeatureOutputField> Fields,
    int OutputSrid);
