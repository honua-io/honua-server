// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// A request refused because its result would exceed a Geometry Service limit. The message is
// returned to the caller as it is, so it says what to change and nothing else.
internal sealed class GeometryServiceLimitException(string message) : ArgumentException(message);
