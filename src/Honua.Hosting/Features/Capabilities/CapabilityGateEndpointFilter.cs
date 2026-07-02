// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Capabilities;

/// <summary>
/// Shared endpoint filter that gates a route (or route group) on a single
/// capability descriptor from the unified capability registry (ADR-0058,
/// Track T5 / #2341). It reads the gated descriptor id from the
/// <see cref="CapabilityGateMetadata"/> attached by <c>WithCapabilityGate</c>,
/// resolves it through <see cref="ICapabilityRegistry"/> with the config-driven
/// experimental precedence (#2339 / T2), and short-circuits the request when the
/// capability resolves <b>experimental-disabled</b>.
/// </summary>
/// <remarks>
/// <para>
/// The filter only short-circuits the <see cref="CapabilityReasonCodes.ExperimentalDisabled"/>
/// outcome — an off-by-default experimental capability whose flag is not set. In
/// that case it returns <c>404 Not Found</c> as <c>application/problem+json</c>
/// with <c>type: honua:capability-experimental-disabled</c> and a hint to enable
/// the capability via <c>Capabilities:Experimental:{id}</c>, so a disabled
/// experimental surface is indistinguishable from an unmapped route.
/// </para>
/// <para>
/// Every other resolution passes through to the endpoint: an enabled capability,
/// an unregistered id, and — deliberately — the flag-on-but-wrong-edition
/// (<see cref="CapabilityReasonCodes.LicenseRequired"/>) outcome, which the
/// existing entitlement gate continues to surface as <c>402 Payment Required</c>.
/// This filter does not change that path.
/// </para>
/// <para>
/// It mirrors the <c>ETagEndpointFilter</c> registration shape — added via
/// <c>AddEndpointFilter&lt;T&gt;</c> and activated from DI — so it is applied at
/// the group level exactly like the other endpoint filters.
/// </para>
/// </remarks>
internal sealed partial class CapabilityGateEndpointFilter : IEndpointFilter
{
    /// <summary>The problem <c>type</c> emitted for an experimental-disabled capability.</summary>
    internal const string ExperimentalDisabledProblemType = "honua:capability-experimental-disabled";

    private readonly ICapabilityRegistry _registry;
    private readonly IOptions<CapabilityFlagOptions> _flags;
    private readonly ILogger<CapabilityGateEndpointFilter> _logger;

    /// <summary>
    /// Creates the capability gate filter.
    /// </summary>
    /// <param name="registry">The unified capability registry to resolve against.</param>
    /// <param name="flags">The config-bound experimental feature-flag switches.</param>
    /// <param name="logger">The logger instance.</param>
    public CapabilityGateEndpointFilter(
        ICapabilityRegistry registry,
        IOptions<CapabilityFlagOptions> flags,
        ILogger<CapabilityGateEndpointFilter> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _flags = flags ?? throw new ArgumentNullException(nameof(flags));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        // The gated descriptor id travels on endpoint metadata (added alongside the
        // filter by WithCapabilityGate). With no marker the filter is inert.
        var gate = httpContext.GetEndpoint()?.Metadata.GetMetadata<CapabilityGateMetadata>();
        if (gate is null)
        {
            return next(context);
        }

        var gateContext = new CapabilityGateContext
        {
            ExperimentalFlags = _flags.Value,
        };

        var resolution = _registry.Resolve(gate.DescriptorId, gateContext);

        // Only the experimental-disabled outcome is a short-circuit here. Enabled and
        // unregistered fall through; the wrong-edition (license-required) outcome is
        // intentionally left to the existing 402 entitlement path.
        if (!resolution.Enabled &&
            string.Equals(resolution.ReasonCode, CapabilityReasonCodes.ExperimentalDisabled, StringComparison.Ordinal))
        {
            Log.ExperimentalDisabled(_logger, gate.DescriptorId, httpContext.Request.Path);
            return ValueTask.FromResult<object?>(CreateExperimentalDisabledProblem(httpContext, gate.DescriptorId));
        }

        return next(context);
    }

    private static IResult CreateExperimentalDisabledProblem(HttpContext httpContext, string descriptorId)
    {
        var detail =
            $"The '{descriptorId}' capability is experimental and disabled. " +
            $"Enable it via the configuration key 'Capabilities:Experimental:{descriptorId}:Enabled=true' " +
            "(or the global switch 'Capabilities:Experimental:Enabled=true').";

        return ProblemDetailsHelpers.CreateProblem(
            httpContext,
            ExperimentalDisabledProblemType,
            StatusCodes.Status404NotFound,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
            detail);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4110, Level = LogLevel.Debug,
            Message = "Capability gate short-circuited {DescriptorId} at {Path}: experimental-disabled")]
        public static partial void ExperimentalDisabled(ILogger logger, string descriptorId, string path);
    }
}
