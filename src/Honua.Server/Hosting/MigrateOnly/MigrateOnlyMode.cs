// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Hosting.MigrateOnly;

/// <summary>
/// How the process was asked to run with respect to <see cref="MigrateOnlyMode.SwitchKey"/>.
/// </summary>
internal enum MigrateOnlyRequest
{
    /// <summary>The switch is unset or false: start the server as usual.</summary>
    NotRequested = 0,

    /// <summary>The switch is true: apply migrations, print the summary and exit.</summary>
    Requested = 1,

    /// <summary>The switch carries a value that is neither true nor false.</summary>
    Invalid = 2,
}

/// <summary>
/// Resolves the migrate-only switch (honua-server#6470). The switch is read once, at process start,
/// from configuration, so <c>HONUA_MIGRATE_ONLY=true</c> in the environment and
/// <c>--HONUA_MIGRATE_ONLY=true</c> on the command line are equivalent. Nothing else in the
/// process consults it.
/// </summary>
internal static class MigrateOnlyMode
{
    /// <summary>
    /// Configuration key (environment variable or command-line switch) that selects migrate-only
    /// mode. honua-iac's serverless migration function already sets it.
    /// </summary>
    internal const string SwitchKey = "HONUA_MIGRATE_ONLY";

    /// <summary>Environment variable the AWS Lambda runtime sets inside every execution environment.</summary>
    internal const string AwsLambdaRuntimeApiKey = "AWS_LAMBDA_RUNTIME_API";

    /// <summary>Environment variable AWS Lambda sets to the function name.</summary>
    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>
    /// Reads the switch. An empty or absent value is <see cref="MigrateOnlyRequest.NotRequested"/>, so
    /// a serving process, including a Lambda function's init, never takes this path unless the
    /// switch is explicitly set.
    /// </summary>
    /// <param name="configuration">The process configuration.</param>
    /// <returns>The resolved request.</returns>
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

    /// <summary>
    /// Whether the process runs inside an AWS Lambda execution environment. Migrate-only mode
    /// refuses to run there: a Lambda init that exits is retried by the runtime, and the
    /// migration-function receipt contract (<c>honua.lambda-migration-run/v1</c>) is not served by this
    /// mode. Run it as a container task or Job instead.
    /// </summary>
    /// <param name="environment">Environment variable accessor.</param>
    /// <returns><see langword="true"/> inside Lambda.</returns>
    internal static bool IsAwsLambda(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !string.IsNullOrWhiteSpace(environment(AwsLambdaRuntimeApiKey))
            || !string.IsNullOrWhiteSpace(environment(AwsLambdaFunctionNameKey));
    }
}
