// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

public static class CapabilityGateResolver
{
    public static CapabilityResolution Resolve(CapabilityDescriptor? descriptor, CapabilityGateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (descriptor is null)
        {
            return new CapabilityResolution(false, CapabilityReasonCodes.NotRegistered);
        }

        if (descriptor.ImplementationStatus == CapabilityImplementationStatus.KnownGap)
        {
            return new CapabilityResolution(false, CapabilityReasonCodes.NotImplemented);
        }

        if (descriptor.Maturity is CapabilityMaturity.Internal or CapabilityMaturity.Experimental or CapabilityMaturity.Preview)
        {
            if (descriptor.RequiresOptIn && !context.ExperimentalFlags.IsExperimentalEnabled(descriptor.Id))
            {
                return new CapabilityResolution(false, CapabilityReasonCodes.ExperimentalDisabled);
            }

            // Non-public or pre-release capability: entitlement/edition still applies on top,
            // so an unlicensed edition fails on edition rather than being masked by the flag.
            if (descriptor.MinimumEdition is { } minimumEdition && context.Edition < minimumEdition)
            {
                return new CapabilityResolution(false, CapabilityReasonCodes.LicenseRequired);
            }
        }

        // Any required opt-in and edition checks are satisfied: enabled.
        return new CapabilityResolution(true, null);
    }
}
