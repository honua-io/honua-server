// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Capabilities;

internal static class CapabilityGateExtensions
{
    internal static RouteGroupBuilder WithCapabilityGate(this RouteGroupBuilder builder, string descriptorId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);

        builder.WithMetadata(new CapabilityGateMetadata(descriptorId));
        builder.AddEndpointFilter<CapabilityGateEndpointFilter>();
        return builder;
    }

    internal static RouteHandlerBuilder WithCapabilityGate(this RouteHandlerBuilder builder, string descriptorId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);

        builder.WithMetadata(new CapabilityGateMetadata(descriptorId));
        builder.AddEndpointFilter<CapabilityGateEndpointFilter>();
        return builder;
    }
}
