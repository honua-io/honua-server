// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

/// <summary>
/// Persists operator-managed field display metadata for published layers.
/// </summary>
public interface ILayerFieldConfigurationStore
{
    /// <summary>
    /// Gets persisted field configuration for a layer ordered by field order.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted field configuration entries.</returns>
    Task<IReadOnlyList<LayerFieldConfiguration>> GetFieldConfigurationsAsync(
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates aliases, domains, and visibility for the supplied fields and returns the full layer configuration.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="updates">Field configuration updates to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated field configuration entries.</returns>
    Task<IReadOnlyList<LayerFieldConfiguration>> UpdateFieldConfigurationsAsync(
        int layerId,
        IReadOnlyList<LayerFieldConfigurationUpdate> updates,
        CancellationToken cancellationToken = default);
}
