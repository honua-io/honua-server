// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Routing.Features.Routing.Providers;

internal static class RoutingTelemetry
{
    public const string ActivitySourceName = "Honua.Routing";

    public static readonly ActivitySource Source = new(ActivitySourceName);
}
