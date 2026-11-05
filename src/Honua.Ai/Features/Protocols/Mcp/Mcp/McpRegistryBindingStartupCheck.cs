// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp;

internal sealed class McpRegistryBindingStartupCheck : IHostedService
{
    private readonly McpDataAccessSurface _surface;
    private readonly ICapabilityRegistry _registry;
    private readonly CapabilityRegistryBindingOptions _options;

    public McpRegistryBindingStartupCheck(
        McpDataAccessSurface surface,
        ICapabilityRegistry registry,
        IOptions<CapabilityRegistryBindingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _surface = surface;
        _registry = registry;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.RegistryBinding)
        {
            return;
        }

        var drift = await McpRegistryCompositionValidator
            .FindDriftAsync(_surface, _registry, cancellationToken)
            .ConfigureAwait(false);
        if (drift.Count > 0)
        {
            throw new InvalidOperationException(
                "The served /mcp catalog is not bound to the capability registry "
                + "(ADR-0058 B2, honua-server#2334). Reconcile the roster, or disable "
                + "the gate with Capabilities:RegistryBinding=false: "
                + string.Join("; ", drift) + ".");
        }

    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
