// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Prompts;

/// <summary>
/// One curated prompt in <see cref="McpPromptCatalog"/>: a stable name, display
/// metadata, the templated arguments it accepts, and the message template that
/// <c>prompts/get</c> renders. Templates use <c>{argumentName}</c> placeholders;
/// a placeholder for an omitted optional argument is replaced with a neutral
/// "not specified" phrase so the rendered prompt stays grammatical.
/// </summary>
internal sealed record McpPromptDefinition(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<McpPromptArgumentDefinition> Arguments,
    string Template)
{
    private const string UnspecifiedOptionalPlaceholder = "(not specified)";

    /// <summary>Projects the definition onto the <c>prompts/list</c> descriptor.</summary>
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

    /// <summary>
    /// Validates required arguments and renders the template into a single
    /// user-role prompt message for <c>prompts/get</c>.
    /// </summary>
    /// <exception cref="GeoprocessingValidationException">
    /// A required argument was omitted or blank.
    /// </exception>
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
        var builder = new StringBuilder(Template);
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

            builder.Replace("{" + argument.Name + "}", value);
        }

        return builder.ToString();
    }
}

/// <summary>
/// A single templated argument a <see cref="McpPromptDefinition"/> accepts.
/// </summary>
internal sealed record McpPromptArgumentDefinition(string Name, string Description, bool Required);
