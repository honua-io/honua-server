// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Prompts;

internal sealed record McpPromptDefinition(
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
        return SubstituteValues(ResolveArgumentValues(arguments));
    }

    private Dictionary<string, string> ResolveArgumentValues(IReadOnlyDictionary<string, string>? arguments)
    {
        var values = new Dictionary<string, string>(Arguments.Count, StringComparer.Ordinal);
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

            values[argument.Name] = hasValue
                ? arguments![argument.Name].Trim()
                : UnspecifiedOptionalPlaceholder;
        }

        return values;
    }

    private string SubstituteValues(Dictionary<string, string> values)
    {
        // One pass over the template. Values are copied as text, so a value
        // that contains a later argument's {name} is not rewritten.
        var template = Template;
        var builder = new StringBuilder(template.Length);
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{')
            {
                builder.Append(template[index]);
                continue;
            }

            var close = template.IndexOf('}', index + 1);
            if (close < 0)
            {
                builder.Append(template[index]);
                continue;
            }

            var name = template.Substring(index + 1, close - index - 1);
            if (!values.TryGetValue(name, out var value))
            {
                builder.Append(template[index]);
                continue;
            }

            builder.Append(value);
            index = close;
        }

        return builder.ToString();
    }
}

internal sealed record McpPromptArgumentDefinition(string Name, string Description, bool Required);
