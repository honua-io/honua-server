// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// Renders the <c>honua gp describe &lt;id&gt;</c> output (GP Devkit P3, issue #2124):
/// the typed parameters, declared outputs, an inputs JSON Schema, and a ready-to-paste
/// example <c>honua gp run</c> command — so an author can treat a process like a typed
/// library function rather than a black box. Rendering is split out as pure functions
/// (no <see cref="Console"/> coupling) so the human and machine views can be unit-tested
/// directly against a <see cref="ProcessDefinition"/>.
/// </summary>
internal static class GpDescribeRenderer
{
    /// <summary>
    /// Renders the human-readable description: header metadata, a typed parameter table,
    /// the declared output artifact kinds, the inputs JSON Schema, and an example
    /// invocation.
    /// </summary>
    /// <param name="definition">The catalog definition to describe.</param>
    /// <returns>The multi-line description text.</returns>
    public static string RenderText(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var builder = new StringBuilder();
        builder.Append("process     : ").AppendLine(definition.ProcessId);
        builder.Append("title       : ").AppendLine(definition.Title);
        builder.Append("category    : ").AppendLine(definition.Category);
        builder.Append("runtime     : ").AppendLine(RuntimeProfiles.Normalize(definition.RuntimeProfile));
        builder.Append("description : ").AppendLine(definition.Description);
        builder.AppendLine();

        builder.AppendLine("parameters:");
        if (definition.Parameters.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var parameter in definition.Parameters)
            {
                var requirement = parameter.Required ? "required" : "optional";
                builder.Append("  ")
                    .Append(parameter.Name)
                    .Append(" [")
                    .Append(MapSchemaType(parameter.ValueType))
                    .Append(", ")
                    .Append(requirement)
                    .Append(']');

                if (parameter.DefaultValue is { } @default)
                {
                    builder.Append(" = ").Append(@default);
                }

                builder.AppendLine();
                builder.Append("      ").AppendLine(parameter.Description);

                if (parameter.AllowedValues is { Count: > 0 } allowed)
                {
                    builder.Append("      allowed: ").AppendLine(string.Join(", ", allowed));
                }
            }
        }

        builder.AppendLine();
        var outputs = definition.OutputArtifactKinds.Count == 0
            ? "(none)"
            : string.Join(", ", definition.OutputArtifactKinds.Select(kind => kind.ToString()));
        builder.Append("outputs     : ").AppendLine(outputs);
        builder.AppendLine();

        builder.AppendLine("inputs JSON Schema:");
        builder.AppendLine(BuildInputSchema(definition).ToJsonString(IndentedOptions));
        builder.AppendLine();

        builder.AppendLine("example:");
        builder.Append("  ").AppendLine(BuildExampleCommand(definition));

        return builder.ToString();
    }

    /// <summary>
    /// Renders the machine-readable descriptor as a single JSON object: id/title/category/
    /// runtime metadata, the inputs JSON Schema, the declared outputs, and an example
    /// command. Built with <see cref="JsonNode"/> so it serializes without reflection
    /// (trim/AOT friendly).
    /// </summary>
    /// <param name="definition">The catalog definition to describe.</param>
    /// <returns>The indented JSON descriptor.</returns>
    public static string RenderJson(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var outputs = new JsonArray();
        foreach (var kind in definition.OutputArtifactKinds)
        {
            outputs.Add(kind.ToString());
        }

        var descriptor = new JsonObject
        {
            ["id"] = definition.ProcessId,
            ["title"] = definition.Title,
            ["description"] = definition.Description,
            ["category"] = definition.Category,
            ["runtimeProfile"] = RuntimeProfiles.Normalize(definition.RuntimeProfile),
            ["inputs"] = BuildInputSchema(definition),
            ["outputs"] = outputs,
            ["example"] = BuildExampleCommand(definition),
        };

        return descriptor.ToJsonString(IndentedOptions);
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Builds a JSON Schema (object) describing the process inputs: one property per
    /// parameter typed from its <see cref="ProcessParameterValueType"/>, the required
    /// names, declared defaults, and finite <c>enum</c> sets where the parameter has an
    /// allowed-value list.
    /// </summary>
    private static JsonObject BuildInputSchema(ProcessDefinition definition)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in definition.Parameters)
        {
            var property = new JsonObject
            {
                ["type"] = MapSchemaType(parameter.ValueType),
                ["description"] = parameter.Description,
            };

            if (parameter.ValueType == ProcessParameterValueType.WkbArray)
            {
                property["items"] = new JsonObject { ["type"] = "string" };
            }

            if (parameter.DefaultValue is { } @default)
            {
                property["default"] = @default;
            }

            if (parameter.AllowedValues is { Count: > 0 } allowed)
            {
                var enumValues = new JsonArray();
                foreach (var value in allowed)
                {
                    enumValues.Add(value);
                }

                property["enum"] = enumValues;
            }

            properties[parameter.Name] = property;

            if (parameter.Required)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = definition.Title + " inputs",
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    /// <summary>
    /// Builds an example <c>honua gp run</c> command line: a <c>--param</c> for every
    /// required parameter (using its default, first allowed value, or a typed placeholder)
    /// so the author can copy, fill the placeholders, and run.
    /// </summary>
    private static string BuildExampleCommand(ProcessDefinition definition)
    {
        var builder = new StringBuilder("honua gp run ");
        builder.Append(definition.ProcessId);

        foreach (var parameter in definition.Parameters.Where(p => p.Required))
        {
            builder.Append(" --param ")
                .Append(parameter.Name)
                .Append('=')
                .Append(ExampleValue(parameter));
        }

        return builder.ToString();
    }

    private static string ExampleValue(ProcessParameterSpec parameter)
    {
        if (parameter.DefaultValue is { Length: > 0 } @default)
        {
            return @default;
        }

        if (parameter.AllowedValues is { Count: > 0 } allowed)
        {
            return allowed[0];
        }

        return parameter.ValueType switch
        {
            ProcessParameterValueType.WholeNumber => "0",
            ProcessParameterValueType.FloatingPoint => "0.0",
            ProcessParameterValueType.Flag => "false",
            ProcessParameterValueType.Srid => "4326",
            ProcessParameterValueType.Wkb => "<base64-wkb>",
            ProcessParameterValueType.WkbArray => "<base64-wkb,...>",
            ProcessParameterValueType.LayerId => "<layer-id>",
            _ => "<" + parameter.Name + ">",
        };
    }

    /// <summary>
    /// Maps a <see cref="ProcessParameterValueType"/> onto its JSON Schema primitive type.
    /// </summary>
    private static string MapSchemaType(ProcessParameterValueType valueType) => valueType switch
    {
        ProcessParameterValueType.WholeNumber => "integer",
        ProcessParameterValueType.Srid => "integer",
        ProcessParameterValueType.FloatingPoint => "number",
        ProcessParameterValueType.Flag => "boolean",
        ProcessParameterValueType.WkbArray => "array",
        _ => "string",
    };
}
