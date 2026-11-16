// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Tools;

// A tool that advertises a closed input schema must refuse the argument names the schema
// does not declare. Dropping them returns an answer the caller did not ask for: a misspelled
// filter yields the unfiltered result with no warning.
internal static class McpDeclaredArgumentGuard
{
    private const int MaxReportedArguments = 10;
    private const int MaxReportedNameLength = 64;

    public static void EnsureDeclared(IMcpTool tool, JsonElement? arguments)
    {
        // Published tools check their own names: they also read routing fields the schema omits.
        if (tool is PublishedOperationTool
            || arguments is not { ValueKind: JsonValueKind.Object } supplied
            || !supplied.EnumerateObject().Any())
        {
            return;
        }

        var schema = tool.Describe().InputSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False)
        {
            return;
        }

        var declared = schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
                ? properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                : [];
        var unknown = supplied.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !declared.Contains(name))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw Refusal(tool.Name, unknown);
        }
    }

    // Names at most ten arguments, each cut to 64 characters, and never a value.
    public static GeoprocessingValidationException Refusal(string toolName, IEnumerable<string> unknownNames)
    {
        var unknown = unknownNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var reported = unknown
            .Take(MaxReportedArguments)
            .Select(name => name.Length > MaxReportedNameLength ? name[..MaxReportedNameLength] + "..." : name);
        var more = unknown.Length > MaxReportedArguments
            ? $" and {unknown.Length - MaxReportedArguments} more"
            : string.Empty;
        return new GeoprocessingValidationException(
            $"Tool '{toolName}' does not accept argument(s): {string.Join(", ", reported)}{more}. "
            + "Use only the properties in the tool's input schema.");
    }
}
