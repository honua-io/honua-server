// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

public sealed record DataResidencyDecision
{
    public required bool Allowed { get; init; }

    public required string Region { get; init; }

    public required DataResidencyPolicy Policy { get; init; }

    public required string Reason { get; init; }

    public static DataResidencyDecision Allow(string region, DataResidencyPolicy policy, string reason) =>
        new() { Allowed = true, Region = region, Policy = policy, Reason = reason };

    public static DataResidencyDecision Deny(string region, DataResidencyPolicy policy, string reason) =>
        new() { Allowed = false, Region = region, Policy = policy, Reason = reason };
}
