// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

public sealed record DataResidencyPolicy
{
    public static readonly DataResidencyPolicy Disabled = new()
    {
        Enforced = false,
        PrimaryRegion = "unspecified",
        AllowedRegions = Array.Empty<string>(),
    };

    public required bool Enforced { get; init; }

    public required string PrimaryRegion { get; init; }

    public required IReadOnlyList<string> AllowedRegions { get; init; }

    public bool IsRegionAllowed(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return false;
        }

        for (var i = 0; i < AllowedRegions.Count; i++)
        {
            if (string.Equals(AllowedRegions[i], region, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
