// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

/// <summary>
/// Configuration descriptor for a federated data source. A descriptor names a remote
/// source, declares its transport <see cref="FederatedSourceKind"/>, and points at the
/// remote service endpoint plus the remote layer/collection that backs a federated join.
/// </summary>
/// <remarks>
/// The descriptor carries only non-secret configuration. Transport credentials are
/// resolved separately through the existing secure-connection / secret-reference
/// infrastructure, so a descriptor is safe to surface through the Admin API and to use in
/// offline query planning. See issue #341.
/// </remarks>
public sealed record FederatedSourceDescriptor
{
    /// <summary>
    /// Gets the stable identifier for the source, unique within the deployment.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the human-readable display name for the source.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the transport family of the remote source.
    /// </summary>
    public required FederatedSourceKind Kind { get; init; }

    /// <summary>
    /// Gets the base endpoint URL of the remote service.
    /// </summary>
    public required Uri Endpoint { get; init; }

    /// <summary>
    /// Gets the remote layer or collection identifier that this source exposes for
    /// federated joins (for example an Esri layer id, an OGC collection id, or a Honua
    /// service/layer id).
    /// </summary>
    public required string RemoteLayer { get; init; }

    /// <summary>
    /// Gets the per-request timeout applied to remote calls. Combined with the circuit
    /// breaker, this bounds the blast radius of a slow or failing remote source.
    /// </summary>
    public required TimeSpan RequestTimeout { get; init; }

    /// <summary>
    /// Gets a value indicating whether the source is currently enabled for federation.
    /// Disabled sources are configurable and inspectable but excluded from query planning.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
