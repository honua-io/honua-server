// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Capabilities;

// Only an experimental-disabled capability returns 404; a wrong-edition result is left for the entitlement gate to return 402.
internal sealed partial class CapabilityGateEndpointFilter : IEndpointFilter
{
    internal const string ExperimentalDisabledProblemType = "honua:capability-experimental-disabled";

    private readonly ICapabilityRegistry _registry;
    private readonly IOptions<CapabilityFlagOptions> _flags;
    private readonly ILogger<CapabilityGateEndpointFilter> _logger;

    public CapabilityGateEndpointFilter(
        ICapabilityRegistry registry,
        IOptions<CapabilityFlagOptions> flags,
        ILogger<CapabilityGateEndpointFilter> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _flags = flags ?? throw new ArgumentNullException(nameof(flags));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            Log.ExperimentalDisabled(_logger, gate.DescriptorId, httpContext.Request.Path.Value ?? string.Empty);
            var maturity = _registry.Find(gate.DescriptorId)?.Maturity;
            return ValueTask.FromResult<object?>(CreateExperimentalDisabledProblem(
                httpContext,
                gate.DescriptorId,
                maturity));
        }

        return next(context);
    }

    private static IResult CreateExperimentalDisabledProblem(
        HttpContext httpContext,
        string descriptorId,
        CapabilityMaturity? maturity)
    {
        var lifecycle = maturity == CapabilityMaturity.Preview ? "preview" : "experimental";
        var detail =
            $"The '{descriptorId}' capability is {lifecycle} and disabled. " +
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
