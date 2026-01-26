// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Models;

public sealed class LayerStyleUpdateRequest
{
    public JsonElement? MapLibreStyle { get; init; }

    public JsonElement? DrawingInfo { get; init; }
}

public sealed class LayerStyleResponse
{
    public JsonElement? MapLibreStyle { get; init; }

    public JsonElement? DrawingInfo { get; init; }
}
