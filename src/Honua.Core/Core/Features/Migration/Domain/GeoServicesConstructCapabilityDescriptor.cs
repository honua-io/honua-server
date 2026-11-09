// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoServicesConstructCapabilityDescriptor
{
    public required string ConstructKey { get; init; }

    public required string AutomationStatus { get; init; }

    public required string Code { get; init; }

    public required string Reason { get; init; }

    public string[] ManualSteps { get; init; } = [];

    public string? UnsupportedAutomationStatus { get; init; }

    public string? UnsupportedCode { get; init; }

    public string? UnsupportedReason { get; init; }

    public string[] UnsupportedManualSteps { get; init; } = [];

    public bool CanTransform { get; init; }

    public bool CanServe { get; init; }

    public bool RequiresCheck { get; init; }

    public string? Notes { get; init; }
}
