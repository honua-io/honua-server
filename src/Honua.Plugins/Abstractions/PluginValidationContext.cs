// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public sealed record PluginValidationContext(
    string ServiceId,
    int LayerId,
    string? ResourceName,
    EditKind EditKind,
    long? ObjectId,
    string? Actor);
