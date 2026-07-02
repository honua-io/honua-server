// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Capabilities;

/// <summary>
/// Endpoint metadata that marks a route (or route group) as gated by a single
/// capability descriptor from the unified capability registry (ADR-0058). It
/// carries the <see cref="DescriptorId"/> the <see cref="CapabilityGateEndpointFilter"/>
/// resolves at request time, and is attached by the
/// <c>WithCapabilityGate(descriptorId)</c> extension (Track T5, #2341).
/// </summary>
/// <remarks>
/// The marker is added as endpoint metadata (in addition to the filter) so the
/// gated descriptor is discoverable by other conventions — OpenAPI/description
/// surfaces and diagnostics can see which capability guards an endpoint without
/// reaching into the filter. It mirrors the way the ETag filter is attached to
/// endpoints in <c>ETagExtensions</c>.
/// </remarks>
internal sealed class CapabilityGateMetadata
{
    /// <summary>
    /// Creates metadata gating an endpoint on the given capability descriptor.
    /// </summary>
    /// <param name="descriptorId">
    /// The stable <c>CapabilityDescriptor.Id</c> this endpoint is gated on (for
    /// example <c>temporal.filtering</c>).
    /// </param>
    public CapabilityGateMetadata(string descriptorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);
        DescriptorId = descriptorId;
    }

    /// <summary>
    /// The stable capability-descriptor id the endpoint is gated on. The filter
    /// resolves this id against the <c>ICapabilityRegistry</c> and short-circuits
    /// the request when the capability resolves experimental-disabled.
    /// </summary>
    public string DescriptorId { get; }
}
