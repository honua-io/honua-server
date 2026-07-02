// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The config-driven gate resolver that fills the <see cref="ICapabilityRegistry.Resolve"/>
/// seam B1 (#2333) left as a pass-through (#2339 / T2). It applies the ADR-0058
/// experimental feature-flag precedence to a single capability descriptor.
/// </summary>
/// <remarks>
/// <para>Resolution precedence:</para>
/// <list type="number">
///   <item><description>
///     Unknown id (null descriptor) → disabled with
///     <see cref="CapabilityReasonCodes.NotRegistered"/> (B1 behaviour, preserved).
///   </description></item>
///   <item><description>
///     <see cref="CapabilityMaturity.Experimental"/> maturity → enabled only when the
///     per-capability or global experimental flag is on; otherwise disabled with
///     <see cref="CapabilityReasonCodes.ExperimentalDisabled"/> (default-off).
///   </description></item>
///   <item><description>
///     Entitlement/edition still applies <b>on top</b>: a flag-on experimental
///     capability whose <see cref="CapabilityDescriptor.MinimumEdition"/> exceeds the
///     context edition fails on edition with
///     <see cref="CapabilityReasonCodes.LicenseRequired"/> — not on the flag.
///   </description></item>
///   <item><description>
///     Non-experimental capabilities → enabled (the B1 pass-through behaviour;
///     entitlement is enforced by the operation surfaces, not this description layer).
///   </description></item>
/// </list>
/// </remarks>
public static class CapabilityGateResolver
{
    /// <summary>
    /// Resolves whether the given descriptor is enabled for the context, applying the
    /// experimental feature-flag precedence.
    /// </summary>
    /// <param name="descriptor">
    /// The capability descriptor, or <c>null</c> when no capability with the requested
    /// id is registered.
    /// </param>
    /// <param name="context">The edition/environment/flag inputs to evaluate against.</param>
    public static CapabilityResolution Resolve(CapabilityDescriptor? descriptor, CapabilityGateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (descriptor is null)
        {
            return new CapabilityResolution(false, CapabilityReasonCodes.NotRegistered);
        }

        if (descriptor.Maturity == CapabilityMaturity.Experimental)
        {
            if (!context.ExperimentalFlags.IsExperimentalEnabled(descriptor.Id))
            {
                return new CapabilityResolution(false, CapabilityReasonCodes.ExperimentalDisabled);
            }

            // Flag-on experimental capability: entitlement/edition still applies on top,
            // so an unlicensed edition fails on edition rather than being masked by the flag.
            if (descriptor.MinimumEdition is { } minimumEdition && context.Edition < minimumEdition)
            {
                return new CapabilityResolution(false, CapabilityReasonCodes.LicenseRequired);
            }
        }

        // Non-experimental (or flag-on experimental with a satisfied edition): enabled.
        return new CapabilityResolution(true, null);
    }
}
