// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class FeatureOverviewResponse
{
    public required string CurrentEdition { get; init; }

    public required FeatureOverviewItem[] Features { get; init; }

    // The roster is read-only because capability writes belong to the config-bound experimental switches.
    public required CapabilityOverviewItem[] Capabilities { get; init; }
}

public sealed class CapabilityOverviewItem
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Maturity { get; init; }

    public required bool Enabled { get; init; }

    public string? ReasonCode { get; init; }
}

public sealed class FeatureOverviewItem
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public required string Category { get; init; }

    public required string Description { get; init; }

    public required bool IsEnabled { get; init; }

    public required string MinimumEdition { get; init; }

    public string? UpgradeMessage { get; init; }
}
