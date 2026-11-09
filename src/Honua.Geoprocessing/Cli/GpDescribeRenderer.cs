// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing.Cli;

internal static class GpDescribeRenderer
{
    public static string RenderText(ProcessDefinition definition) => RenderText(definition, null);

    public static string RenderText(ProcessDefinition definition, GpRunnability? runnability)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var builder = new StringBuilder();
        builder.Append("process     : ").AppendLine(definition.ProcessId);
        builder.Append("title       : ").AppendLine(definition.Title);
        builder.Append("category    : ").AppendLine(definition.Category);
        builder.Append("runtime     : ").AppendLine(RuntimeProfiles.Normalize(definition.RuntimeProfile));
        builder.Append("execution   : ").AppendLine(definition.ExecutionKind.ToString());
        builder.Append("dependency  : ").AppendLine(definition.ConfigurationDependency ?? "See input requirements; registration does not verify dependencies.");
        if (definition.ExecutionCapabilityReason is { } reason)
        {
            builder.Append("availability: ").AppendLine(reason);
        }
        if (runnability is not null)
        {
            builder.Append("mode        : ").AppendLine(runnability.ExecutionModeLabel);
            builder.Append("runnable    : ").AppendLine(runnability.RunnableHere ? "yes" : $"no — blocked by {runnability.BlockedBy}");
            foreach (var prerequisite in runnability.Prerequisites)
            {
                builder.Append("  needs     : ").Append(prerequisite.Name).Append(" [").Append(prerequisite.Status).Append("] ").AppendLine(prerequisite.Detail);
            }

            builder.Append("targets     : ").AppendLine(string.Join(", ", runnability.SupportedTargets));
        }
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

                if (runnability is not null && GpRunnabilityAssessor.ExampleValues(definition)[parameter.Name] is { } example)
                {
                    builder.Append("      example: ").AppendLine(example.GetValue<string>());
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

        builder.AppendLine("example (replace placeholders using the parameter constraints):");
        builder.Append("  ").AppendLine(BuildExampleCommand(definition));

        return builder.ToString();
    }

    public static string RenderJson(ProcessDefinition definition) => RenderJson(definition, null);

    public static string RenderJson(ProcessDefinition definition, GpRunnability? runnability)
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
            ["executionKind"] = definition.ExecutionKind.ToString(),
            ["supportedExecutionModes"] = definition.SupportedExecutionModes.ToString(),
            ["supportedEntryPoints"] = definition.SupportedEntryPoints.ToString(),
            ["configurationDependency"] = definition.ConfigurationDependency,
            ["executionCapabilityReason"] = definition.ExecutionCapabilityReason,
            ["dependenciesVerified"] = false,
            ["inputs"] = BuildInputSchema(definition),
            ["outputs"] = outputs,
            ["example"] = BuildExampleCommand(definition),
        };
        if (runnability is not null)
        {
            runnability.WriteTo(descriptor);
            descriptor["parameterExamples"] = GpRunnabilityAssessor.ExampleValues(definition);
        }

        return descriptor.ToJsonString(IndentedOptions);
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

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
            ProcessParameterValueType.WholeNumber => "<integer>",
            ProcessParameterValueType.FloatingPoint => "<number>",
            ProcessParameterValueType.Flag => "false",
            ProcessParameterValueType.Srid => "<srid>",
            ProcessParameterValueType.Wkb => "<base64-wkb>",
            ProcessParameterValueType.WkbArray => "<base64-wkb,...>",
            ProcessParameterValueType.LayerId => "<layer-id>",
            _ => "<" + parameter.Name + ">",
        };
    }

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
