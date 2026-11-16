// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public sealed class MetadataReleaseOperationOptions
{
    public const string SectionName = "ControlPlane:MetadataRelease";

    public TimeSpan OperationRetention { get; set; } = TimeSpan.FromDays(30);

    public MetadataReleaseFaultInjectionOptions FaultInjection { get; set; } = new();
}

public sealed class MetadataReleaseFaultInjectionOptions
{
    public bool Enabled { get; set; }

    public bool ForceSmokeFailure { get; set; }

    public IReadOnlyList<string> AllowedEnvironments { get; set; } = new[] { "staging", "dev", "demo-staging", "demo-dev" };

    public string Reason { get; set; } = "Injected smoke failure (Demo B safe-rollback fault injection).";

    public bool ShouldFailSmoke(string? targetEnvironment)
    {
        if (!Enabled || !ForceSmokeFailure || string.IsNullOrWhiteSpace(targetEnvironment))
        {
            return false;
        }

        return AllowedEnvironments.Any(allowed => string.Equals(allowed, targetEnvironment, StringComparison.OrdinalIgnoreCase));
    }
}
