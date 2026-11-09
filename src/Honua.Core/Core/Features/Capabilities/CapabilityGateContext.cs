// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Capabilities;

public sealed record CapabilityGateContext
{
    public HonuaEdition Edition { get; init; } = HonuaEdition.Community;

    public string? DeploymentEnvironment { get; init; }

    public CapabilityFlagOptions ExperimentalFlags { get; init; } = new();

    public static CapabilityGateContext Default { get; } = new();
}
