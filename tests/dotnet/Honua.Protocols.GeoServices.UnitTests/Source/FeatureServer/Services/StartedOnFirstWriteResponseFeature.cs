// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http.Features;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

internal sealed class StartedOnFirstWriteResponseFeature(MemoryStream body) : HttpResponseFeature
{
    public override bool HasStarted => body.Length > 0;
}
