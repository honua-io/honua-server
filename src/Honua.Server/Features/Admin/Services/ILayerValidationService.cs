// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin.Services;

internal interface ILayerValidationService
{
    Task<ResourceValidationResult<LayerValidationResponse>> ValidateLayerAsync(
        int storageLayerId,
        CancellationToken cancellationToken = default);
}
