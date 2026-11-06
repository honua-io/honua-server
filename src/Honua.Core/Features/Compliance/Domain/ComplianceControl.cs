// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

public sealed record ComplianceControl
{
    public required string ControlId { get; init; }

    public required ComplianceFramework Framework { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<ComplianceDependency> Dependencies { get; init; } = Array.Empty<ComplianceDependency>();

    // Cross-references are free text and are not checked against the catalog.
    public IReadOnlyList<string> RelatedControls { get; init; } = Array.Empty<string>();
}

public enum ComplianceDependency
{
    AuditLog = 0,

    Sso = 1,

    Rbac = 2,

    EncryptionAtRest = 3,

    EncryptionInTransit = 4,

    DataResidency = 5,
}
