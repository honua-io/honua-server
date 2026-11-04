// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices;

public static class GeoServicesContract
{
    /// <summary>
    /// The rolled-up GeoServices REST wire-contract version (semver). 2.0.0 stops
    /// accepting the concatenated attribute-mosaic token; callers send
    /// <c>esriMosaicAttribute</c>.
    /// </summary>
    public const string Version = "2.0.0";
}
