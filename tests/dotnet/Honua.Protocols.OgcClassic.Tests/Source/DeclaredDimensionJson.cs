// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

internal static class DeclaredDimensionJson
{
    internal const string WmsTime =
        """{"dimensions":[{"name":"time","units":"ISO8601","multipleValues":true,"nearestValue":true,"default":"2000-01-01T00:00:30Z","extent":"2000-01-01T00:00:00Z/2000-01-01T00:01:00Z/PT5S"}]}""";

    internal const string WmsElevation =
        """{"dimensions":[{"name":"elevation","units":"CRS:88","unitSymbol":"m","nearestValue":true,"default":"500","extent":"500,490,480"}]}""";

    internal const string WmtsElevationAndScenario =
        """{"dimensions":[{"identifier":"elevation","values":["100","200","300"],"default":"100","supportsCurrent":true,"current":"300"},{"identifier":"scenario","values":["winter","summer"],"default":"winter"}]}""";

    internal const string WmtsNotQueryable = """{"queryable":false}""";
}
