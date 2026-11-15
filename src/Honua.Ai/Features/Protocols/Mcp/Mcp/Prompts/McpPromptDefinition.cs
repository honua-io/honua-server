// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Prompts;

internal sealed partial record McpPromptDefinition(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<McpPromptArgumentDefinition> Arguments,
    string Template)
{
    private const string UnspecifiedOptionalPlaceholder = "(not specified)";

    public McpPromptDescriptor Describe() => new()
    {
        Name = Name,
        Title = Title,
        Description = Description,
        Arguments = Arguments
            .Select(a => new McpPromptArgument
            {
                Name = a.Name,
                Description = a.Description,
                Required = a.Required,
            })
            .ToList(),
    };

    public McpPromptGetResult Render(IReadOnlyDictionary<string, string>? arguments)
    {
        var rendered = Substitute(arguments);
        return new McpPromptGetResult
        {
            Description = Description,
            Messages =
            [
                new McpPromptMessage
                {
                    Role = "user",
                    Content = new McpContentBlock { Type = "text", Text = rendered },
                },
            ],
        };
    }

    private string Substitute(IReadOnlyDictionary<string, string>? arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in Arguments)
        {
            var hasValue = arguments is not null
                && arguments.TryGetValue(argument.Name, out var raw)
                && !string.IsNullOrWhiteSpace(raw);

            if (argument.Required && !hasValue)
            {
                throw new GeoprocessingValidationException(
                    $"Prompt '{Name}' requires argument '{argument.Name}'.");
            }

            var value = hasValue
                ? arguments![argument.Name].Trim()
                : UnspecifiedOptionalPlaceholder;

            values[argument.Name] = value;
        }

        return PlaceholderPattern().Replace(Template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    [GeneratedRegex(@"\{([^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}

internal sealed record McpPromptArgumentDefinition(string Name, string Description, bool Required);
