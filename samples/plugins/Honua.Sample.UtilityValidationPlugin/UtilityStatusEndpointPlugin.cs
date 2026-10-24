// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Plugins.Abstractions;

namespace Honua.Sample.UtilityValidationPlugin;

/// <summary>
/// Phase-2 reference plugin (issue #1562) demonstrating the <see cref="ICustomEndpoint"/> extension
/// point. Contributes a single read-only REST route the host mounts under
/// <c>/plugins/utility/status</c> via <c>MapHonuaPluginEndpoints()</c>. It declares the
/// <see cref="PluginCapability.CustomEndpoints"/> capability required to contribute routes.
/// </summary>
[Plugin("utility-status-endpoint", "1.0.0",
    Description = "Exposes a read-only plugin status route.",
    Capabilities = PluginCapability.CustomEndpoints)]
public sealed class UtilityStatusEndpointPlugin : ICustomEndpoint
{
    /// <inheritdoc />
    public IReadOnlyList<string> Methods { get; } = ["GET"];

    /// <inheritdoc />
    public string Pattern => "utility/status";

    /// <inheritdoc />
    public bool RequiresAuthorization => false;

    /// <inheritdoc />
    public ValueTask<PluginEndpointResponse> HandleAsync(
        PluginEndpointRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginEndpointResponse.Json("{\"status\":\"ok\",\"plugin\":\"utility-status-endpoint\"}"));
}
