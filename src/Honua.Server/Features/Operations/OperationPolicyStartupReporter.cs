// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Logs the effective typed operation policy once at startup, for example
/// <c>Operation policy: 0 operator rules + 2 built-in rules, default Deny</c>, so an operator can
/// see whether the built-in Production administrator rules are in force without reading code.
/// </summary>
internal sealed partial class OperationPolicyStartupReporter(
    IServiceProvider services,
    ILogger<OperationPolicyStartupReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        switch (services.GetService<IOperationPolicyDecisionPoint>())
        {
            case CanonicalOperationPolicyDecisionPoint canonical:
                Log.EffectivePolicy(logger, canonical.TypedPolicy.Describe(), canonical.TypedPolicy.BuiltInRules);
                break;
            case { } custom:
                Log.CustomPolicy(logger, custom.GetType().Name);
                break;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static partial class Log
    {
        [LoggerMessage(7429, LogLevel.Information, "Operation policy: {Policy} (builtInRules: {BuiltInRules})")]
        public static partial void EffectivePolicy(ILogger logger, string policy, bool builtInRules);

        [LoggerMessage(7430, LogLevel.Information, "Operation policy: host-supplied decision point {DecisionPoint}")]
        public static partial void CustomPolicy(ILogger logger, string decisionPoint);
    }
}
