// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.ControlPlane;

/// <summary>
/// Resolves the Lambda function, alias, region, and endpoint a deploy and a governed
/// restart both use. The keys match the registered deploy target, including the
/// <c>aws.lambda.*</c> names and the <c>TargetName</c> / <c>live</c> fallbacks.
/// </summary>
public static partial class LambdaTargetAddress
{
    [GeneratedRegex("^arn:(aws[a-zA-Z-]*)?:lambda:(?<region>[^:]+):", RegexOptions.CultureInvariant)]
    private static partial Regex ArnRegionPattern();

    /// <summary>Function name from the deploy-target parameters, then <paramref name="targetName"/>.</summary>
    public static string ResolveFunctionName(string? targetName, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Read(parameters, "aws.lambda.function_name")
            ?? Read(parameters, "lambda.function_name")
            ?? Read(parameters, "lambda.alias_function_name")
            ?? (string.IsNullOrWhiteSpace(targetName) ? string.Empty : targetName.Trim());
    }

    /// <summary>Alias name from the deploy-target parameters. Unpublished aliases fall back to <c>live</c>.</summary>
    public static string ResolveAliasName(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Read(parameters, "aws.lambda.alias_name")
            ?? Read(parameters, "lambda.alias_name")
            ?? Read(parameters, "lambda.alias")
            ?? "live";
    }

    /// <summary>Explicit region, then the region embedded in a function or alias ARN.</summary>
    public static string? ResolveRegion(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var explicitRegion = Read(parameters, "aws.region") ?? Read(parameters, "lambda.region");
        if (!string.IsNullOrWhiteSpace(explicitRegion))
        {
            return explicitRegion;
        }

        var resourceId = Read(parameters, "target.resource_id")
            ?? Read(parameters, "lambda.function_arn")
            ?? Read(parameters, "lambda.alias_arn");
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var match = ArnRegionPattern().Match(resourceId);
        return match.Success ? match.Groups["region"].Value : null;
    }

    /// <summary>Opt-in service endpoint. Unset means the SDK regional endpoint.</summary>
    public static string? ResolveServiceUrl(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Read(parameters, "aws.service_url") ?? Read(parameters, "lambda.service_url");
    }

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
