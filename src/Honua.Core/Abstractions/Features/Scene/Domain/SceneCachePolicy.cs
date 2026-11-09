// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

// A private response must not be stored by a shared cache.
public sealed record SceneCachePolicy(int MaxAgeSeconds, bool NoStore)
{
    public static readonly SceneCachePolicy Default = new(3600, false);
}
