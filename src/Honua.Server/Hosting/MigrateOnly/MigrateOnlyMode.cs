// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Hosting.MigrateOnly;

internal enum MigrateOnlyRequest
{
    NotRequested = 0,

    Requested = 1,

    Invalid = 2,
}

internal static class MigrateOnlyMode
{
    internal const string SwitchKey = "HONUA_MIGRATE_ONLY";

    internal const string AwsLambdaRuntimeApiKey = "AWS_LAMBDA_RUNTIME_API";

    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

    internal static MigrateOnlyRequest Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[SwitchKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return MigrateOnlyRequest.NotRequested;
        }

        return raw.Trim() switch
        {
            var value when bool.TryParse(value, out var enabled) =>
                enabled ? MigrateOnlyRequest.Requested : MigrateOnlyRequest.NotRequested,
            "1" => MigrateOnlyRequest.Requested,
            "0" => MigrateOnlyRequest.NotRequested,
            _ => MigrateOnlyRequest.Invalid,
        };
    }

    internal static bool IsAwsLambda(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !string.IsNullOrWhiteSpace(environment(AwsLambdaRuntimeApiKey))
            || !string.IsNullOrWhiteSpace(environment(AwsLambdaFunctionNameKey));
    }
}
