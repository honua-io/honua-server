// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

// Admin requests accept a renamed id under its deprecated name too, so published clients keep
// working. The value is required under one of the two names, and both must agree when sent.
internal readonly record struct LegacyIdAlias(string Name, string Alias)
{
    public const int Absent = -1;

    public static readonly LegacyIdAlias StorageLayerId = new("storageLayerId", "layerId");

    public static readonly LegacyIdAlias RelatedStorageLayerId = new("relatedStorageLayerId", "relatedLayerId");

    public static readonly LegacyIdAlias ServiceLayerIndex = new("serviceLayerIndex", "layerId");

    public string? Error(int? value, int? legacyValue)
        => (value, legacyValue) switch
        {
            (null, null) => $"{Name} is required.",
            ({ } current, { } legacy) when current != legacy => $"{Name} and its deprecated alias {Alias} disagree; send {Name} only.",
            _ => null,
        };
}
