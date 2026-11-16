// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Plugins.Abstractions;

namespace Honua.Sample.UtilityValidationPlugin;

[Plugin("utility-status-endpoint", "1.0.0",
    Description = "Exposes a read-only plugin status route.",
    Capabilities = PluginCapability.CustomEndpoints)]
public sealed class UtilityStatusEndpointPlugin : ICustomEndpoint
{
    public IReadOnlyList<string> Methods { get; } = ["GET"];

    public string Pattern => "utility/status";

    public bool RequiresAuthorization => false;

    public ValueTask<PluginEndpointResponse> HandleAsync(
        PluginEndpointRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginEndpointResponse.Json("{\"status\":\"ok\",\"plugin\":\"utility-status-endpoint\"}"));
}
