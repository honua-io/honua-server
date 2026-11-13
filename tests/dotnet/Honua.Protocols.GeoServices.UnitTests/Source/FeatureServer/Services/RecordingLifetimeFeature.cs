// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http.Features;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

internal sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
{
    public bool Aborted { get; private set; }

    public CancellationToken RequestAborted { get; set; }

    public void Abort() => Aborted = true;
}
