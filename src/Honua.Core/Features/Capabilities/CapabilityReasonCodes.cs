// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Stable, machine-readable reason codes returned by
/// <see cref="ICapabilityRegistry.Resolve"/> / <see cref="CapabilityGateResolver"/>
/// when a capability resolves <c>Enabled = false</c>. These are the registry-layer
/// codes shared by every downstream gate (#2339 / T2 onward); the <c>/capabilities</c>
/// manifest surface keeps its own richer availability codes and reuses the same
/// string values where they overlap (for example <see cref="LicenseRequired"/>).
/// </summary>
public static class CapabilityReasonCodes
{
    /// <summary>No capability with the requested id is registered.</summary>
    public const string NotRegistered = "capability-not-registered";

    /// <summary>
    /// The capability is <see cref="CapabilityMaturity.Experimental"/> and neither the
    /// per-capability nor the global experimental flag is enabled, so it is off by
    /// default (#2339 / T2).
    /// </summary>
    public const string ExperimentalDisabled = "experimental-disabled";

    /// <summary>
    /// The active edition does not meet the capability's
    /// <see cref="CapabilityDescriptor.MinimumEdition"/>. Mirrors the existing
    /// license/entitlement reason the <c>/capabilities</c> manifest emits for a
    /// wrong-edition capability, so an entitlement failure is never masked by the
    /// experimental flag.
    /// </summary>
    public const string LicenseRequired = "license-required";
}
