// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Capabilities;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Import;

namespace Honua.Server.Features.Admin;

/// <summary>Typed refusal when a frozen host cannot finish a large layer publish (honua-server#5948).</summary>
internal static class LayerPublishRefusals
{
    internal const string Detail =
        "This host freezes after the response, so a publish above the row threshold is refused instead of being rolled back when the gateway times out.";

    internal const string Remediation =
        "Publish this layer from a long-lived host. With Redis the durable job runner drains the publish; without Redis the host runs it in-process. Tables at or under LayerPublishing:AsyncPublishRowThreshold still publish in the request.";

    internal static IResult ToResult(HttpContext context)
        => ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
            context,
            Detail,
            LayerPublishRunnerDecision.RunnerDependency,
            Remediation,
            CapabilityUnavailableCodes.RedisRemediationRef,
            CapabilityUnavailableCodes.DurableJobsCapability,
            BackgroundImportRunnerDecision.DurableRunnerRequiredReason);

    internal static CapabilityUnavailableException Unavailable()
        => new(
            Detail,
            LayerPublishRunnerDecision.RunnerDependency,
            Remediation,
            CapabilityUnavailableCodes.RedisRemediationRef,
            CapabilityUnavailableCodes.DurableJobsCapability);
}
