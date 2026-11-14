// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal static class OpsHeartbeatRegistration
{
    internal static void AddOpsHeartbeat(this IServiceCollection services, IConfiguration configuration)
    {
#if !HONUA_EXCLUDE_AWS
        if (!ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            return;
        }

        var stack = configuration["Observability:Heartbeat:StackName"];
        if (string.IsNullOrWhiteSpace(stack))
        {
            return;
        }

        if (stack.Length > 128 || !char.IsAsciiLetter(stack[0]) ||
            stack.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new InvalidOperationException("Observability:Heartbeat:StackName must be a valid CloudFormation stack name.");
        }

        AwsOpsHeartbeatPublisher.Register(services, configuration["Observability:Heartbeat:Region"]);
        services.AddSingleton<OpsHeartbeatCoordinator>();
        services.AddHostedService<OpsHeartbeatService>();
#endif
    }
}
