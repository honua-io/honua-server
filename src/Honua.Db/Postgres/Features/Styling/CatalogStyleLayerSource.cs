// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Db.Postgres.Features.Styling;

internal static class CatalogStyleLayerSource
{
    public static string GetId(int layerId) => $"layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    public static string GetTileUrl(int layerId) => $"/tiles/{layerId.ToString(CultureInfo.InvariantCulture)}/{{z}}/{{x}}/{{y}}.mvt";
}
