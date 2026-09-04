// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.GPServer.Models;

/// <summary>Esri GP output reference shape for a hosted result.</summary>
internal sealed class GPResultUrlValue
{
    public string Url { get; init; } = string.Empty;
}
