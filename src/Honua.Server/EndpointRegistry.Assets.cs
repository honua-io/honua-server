// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    private static IReadOnlyList<EndpointDefinition> AssetEndpoints =>
    [
        new("GET", "/api/v1/assets/profiles"),
        new("POST", "/api/v1/assets/profiles"),
        new("GET", "/api/v1/assets/profiles/{profileId}"),
        new("PUT", "/api/v1/assets/profiles/{profileId}"),
    ];
}
