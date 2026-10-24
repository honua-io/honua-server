// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Abstractions;

/// <summary>
/// Shared feature query execution abstraction used by non-FeatureServer protocols.
/// </summary>
internal interface IFeatureQueryDispatcher
{
    Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        CancellationToken cancellationToken = default);

    Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        string? requiredProtocol,
        CancellationToken cancellationToken = default);
}
