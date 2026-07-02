// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Capabilities;

/// <summary>
/// Extension methods for gating endpoints on a capability descriptor from the
/// unified capability registry (ADR-0058, Track T5 / #2341). They attach the
/// <see cref="CapabilityGateMetadata"/> marker plus the shared
/// <see cref="CapabilityGateEndpointFilter"/>, mirroring the <c>WithETag</c>
/// pattern in <c>ETagExtensions</c>.
/// </summary>
/// <remarks>
/// Apply the gate at the <b>group</b> level so every endpoint in the group is
/// covered by one filter registration:
/// <code>
/// var group = app.MapGroup("/some/feature").WithCapabilityGate("some.capability");
/// </code>
/// When the gated capability resolves experimental-disabled, the filter
/// short-circuits with <c>404</c>; every other outcome (enabled, unregistered,
/// or wrong-edition) passes through to the endpoint and its existing gates.
/// </remarks>
internal static class CapabilityGateExtensions
{
    /// <summary>
    /// Gates every endpoint in a route group on the given capability descriptor.
    /// </summary>
    /// <param name="builder">The route group builder.</param>
    /// <param name="descriptorId">
    /// The stable <c>CapabilityDescriptor.Id</c> to gate on (for example
    /// <c>temporal.filtering</c>).
    /// </param>
    /// <returns>The route group builder for chaining.</returns>
    internal static RouteGroupBuilder WithCapabilityGate(this RouteGroupBuilder builder, string descriptorId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);

        builder.WithMetadata(new CapabilityGateMetadata(descriptorId));
        builder.AddEndpointFilter<CapabilityGateEndpointFilter>();
        return builder;
    }

    /// <summary>
    /// Gates a single endpoint on the given capability descriptor.
    /// </summary>
    /// <param name="builder">The route handler builder.</param>
    /// <param name="descriptorId">
    /// The stable <c>CapabilityDescriptor.Id</c> to gate on (for example
    /// <c>temporal.filtering</c>).
    /// </param>
    /// <returns>The route handler builder for chaining.</returns>
    internal static RouteHandlerBuilder WithCapabilityGate(this RouteHandlerBuilder builder, string descriptorId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);

        builder.WithMetadata(new CapabilityGateMetadata(descriptorId));
        builder.AddEndpointFilter<CapabilityGateEndpointFilter>();
        return builder;
    }
}
