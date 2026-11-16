// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

internal static class Wfs20FilterCapabilitiesFactory
{
    public static FilterCapabilities Build()
    {
        return Wfs20Handler.BuildFilterCapabilities();
    }
}
