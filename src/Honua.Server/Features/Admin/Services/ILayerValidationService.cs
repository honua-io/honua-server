// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin.Services;

internal interface ILayerValidationService
{
    Task<LayerValidationResponse?> ValidateLayerAsync(
        int layerId,
        CancellationToken cancellationToken = default);
}
