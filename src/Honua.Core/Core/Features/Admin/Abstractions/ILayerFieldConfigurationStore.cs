// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

public interface ILayerFieldConfigurationStore
{
    Task<IReadOnlyList<LayerFieldConfiguration>> GetFieldConfigurationsAsync(
        int layerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LayerFieldConfiguration>> UpdateFieldConfigurationsAsync(
        int layerId,
        IReadOnlyList<LayerFieldConfigurationUpdate> updates,
        CancellationToken cancellationToken = default);
}
