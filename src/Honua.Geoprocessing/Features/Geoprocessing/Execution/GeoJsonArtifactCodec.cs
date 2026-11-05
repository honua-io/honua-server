// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Honua.Geoprocessing.Execution;

internal static class GeoJsonArtifactCodec
{
    public static GeoJsonReader CreateReader()
        => new(
            NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326),
            new JsonSerializerSettings(),
            dimension: 3);

    public static GeoJsonWriter CreateWriter()
        => new() { Dimension = 3 };
}
