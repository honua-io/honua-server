// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.SimpleSystemsManagement;
using Honua.Core.Features.Observability.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Cloud.Aws.Features.Monitoring;

internal static class AwsOpsWatchdogServiceCollectionExtensions
{
    public static IServiceCollection AddAwsOpsWatchdogKillSwitch(this IServiceCollection services, IConfiguration configuration)
    {
        var parameterName = configuration["Observability:OpsFindings:Watchdog:ModeParameterName"];
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return services;
        }

        var resumeMode = configuration["Observability:OpsFindings:Watchdog:ResumeMode"] ?? "Act";
        if (resumeMode is not ("Act" or "NotifyOnly" or "Off"))
        {
            throw new InvalidOperationException("Observability:OpsFindings:Watchdog:ResumeMode must be Act, NotifyOnly or Off.");
        }

        if (configuration.GetValue<bool>("Observability:OpsFindings:Autonomy:KillSwitchEnabled"))
        {
            resumeMode = "Off";
        }

        var mode = new AwsOpsWatchdogMode(parameterName, resumeMode);
        services.TryAddSingleton<IAmazonSimpleSystemsManagement>(_ => new AmazonSimpleSystemsManagementClient());
        services.TryAddSingleton<IOpsWatchdogKillSwitch>(provider => new AwsOpsWatchdogKillSwitch(
            provider.GetRequiredService<IAmazonSimpleSystemsManagement>().PutParameterAsync, mode));
        return services;
    }
}
