// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Server.Features.Styling;

/// <summary>Shares canonical catalog-style validation with draft style authoring.</summary>
internal sealed class MapStyleValidator : IMapStyleValidator
{
    public bool TryValidate(JsonElement style, out string? error) =>
        MapLibreStyleNormalizer.TryValidateStandalone(style, out error);
}
