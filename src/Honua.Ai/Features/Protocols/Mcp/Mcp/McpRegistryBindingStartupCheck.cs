// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Startup composition conformance check (ADR-0058 B2, honua-server#2334): when
/// <see cref="CapabilityRegistryBindingOptions.RegistryBinding"/> is enabled
/// (the default, and on in CI), it asserts at host start that the served
/// <c>/mcp</c> catalog is bound to the <see cref="ICapabilityRegistry"/> — no tool
/// or resource is served without a registry descriptor — and fails fast otherwise
/// so drift surfaces in CI rather than as a silent contract divergence.
/// </summary>
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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.RegistryBinding)
        {
            return Task.CompletedTask;
        }

        var drift = McpRegistryCompositionValidator.FindDrift(_surface, _registry);
        if (drift.Count > 0)
        {
            throw new InvalidOperationException(
                "The served /mcp catalog is not bound to the capability registry "
                + "(ADR-0058 B2, honua-server#2334). Reconcile the roster, or disable "
                + "the gate with Capabilities:RegistryBinding=false: "
                + string.Join("; ", drift) + ".");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
