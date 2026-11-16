// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.ControlPlane;

public static partial class LambdaTargetAddress
{
    [GeneratedRegex("^arn:(aws[a-zA-Z-]*)?:lambda:(?<region>[^:]+):", RegexOptions.CultureInvariant)]
    private static partial Regex ArnRegionPattern();

    public static string ResolveFunctionName(string? targetName, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Read(parameters, "aws.lambda.function_name")
            ?? Read(parameters, "lambda.function_name")
            ?? Read(parameters, "lambda.alias_function_name")
            ?? (string.IsNullOrWhiteSpace(targetName) ? string.Empty : targetName.Trim());
    }

    public static string ResolveAliasName(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Read(parameters, "aws.lambda.alias_name")
            ?? Read(parameters, "lambda.alias_name")
            ?? Read(parameters, "lambda.alias")
            ?? "live";
    }

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

    public static string? ResolveServiceUrl(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Read(parameters, "aws.service_url") ?? Read(parameters, "lambda.service_url");
    }

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
