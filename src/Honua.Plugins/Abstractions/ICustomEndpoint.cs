// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public interface ICustomEndpoint
{
    IReadOnlyList<string> Methods { get; }

    string Pattern { get; }

    bool RequiresAuthorization { get; }

    ValueTask<PluginEndpointResponse> HandleAsync(PluginEndpointRequest request, CancellationToken cancellationToken);
}
