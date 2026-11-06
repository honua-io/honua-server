// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance;

public sealed class ComplianceOptions
{
    public const string SectionName = "Compliance";

    public bool Soc2ReadinessClaimed { get; set; } = true;

    public bool FedRampReadinessClaimed { get; set; } = true;

    public string PrimaryRegion { get; set; } = "unspecified";

    public ComplianceResidencyOptions DataResidency { get; set; } = new();

    public ComplianceEncryptionOptions Encryption { get; set; } = new();

    // Use an override when audit, SSO, or RBAC is supplied by a sidecar the gate cannot probe.
    public ComplianceDependencyOverrides DependencyOverrides { get; set; } = new();
}

public sealed class ComplianceResidencyOptions
{
    public bool Enforced { get; set; }

    public string PrimaryRegion { get; set; } = string.Empty;

    public List<string> AllowedRegions { get; set; } = new();
}

public sealed class ComplianceEncryptionOptions
{
    // The default algorithms match the connection-encryption service so the report describes the path that actually runs.
    public List<string> Algorithms { get; set; } = new()
    {
        "aes-256-gcm",
        "pbkdf2-sha256-100000",
    };

    public bool FipsModeAttested { get; set; }
}

public sealed class ComplianceDependencyOverrides
{
    public bool? AuditLogConfigured { get; set; }

    public bool? SsoConfigured { get; set; }

    // A null RBAC override means not attested; an in-memory role store is not proof that endpoint policies are enforced.
    public bool? RbacConfigured { get; set; }

    public bool? TransportEncryptionAttested { get; set; }

    public bool? DataResidencyAttested { get; set; }
}
