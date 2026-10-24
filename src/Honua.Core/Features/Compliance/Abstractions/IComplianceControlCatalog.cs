// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Source of compliance control metadata. The default implementation is an in-memory
/// catalog seeded with the SOC 2 / FedRAMP controls the server's evidence collector
/// understands — implementations are free to add or override for managed deployments.
/// </summary>
public interface IComplianceControlCatalog
{
    /// <summary>All controls known to this deployment, in stable (framework, id) order.</summary>
    IReadOnlyList<ComplianceControl> Controls { get; }

    /// <summary>Returns the control with the supplied id or <c>null</c> when not found.</summary>
    ComplianceControl? GetControl(string controlId);
}
