// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.Ogc.Api.Processes.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Contract for the suite-owned OGC API Processes echo fixture. Registration is
/// deliberately gated on the exact certification profile, Test environment, and
/// standalone test-infrastructure opt-in; normal hosts never add it to the catalog.
/// </summary>
internal static class OgcProcessesCiteEchoFixture
{
    internal const string ProcessId = "honua-cite-echo";
    internal const string ProfileName = "ogcapi-processes10";
    internal const string ProfileConfigurationKey = "OgcProcesses:CertificationProfile";
    internal const string TestInfrastructureConfigurationKey = "HONUA_REGISTER_TEST_INFRASTRUCTURE";
    internal const string DataUriPrefix = "data:application/json;base64,";

    internal static readonly ImmutableArray<string> OutputIds =
        ["literal", "object", "binary", "mixed", "array", "bbox"];

    internal static readonly ProcessDefinition Definition = new()
    {
        ProcessId = ProcessId,
        Title = "CITE Echo",
        Description = "Deterministically echoes the pinned OGC API Processes ETS input families.",
        Category = "certification",
        Parameters =
        [
            Parameter("literal", "Literal", "Plain string value.", required: true),
            Parameter("object", "Object", "JSON object value."),
            Parameter("binary", "Binary", "Inline or referenced binary descriptor."),
            Parameter("mixed", "Mixed", "String or JSON object value."),
            Parameter("array", "Array", "JSON array value."),
            Parameter("bbox", "Bounding Box", "OGC bounding-box value."),
            new ProcessParameterSpec
            {
                Name = "pause",
                DisplayName = "Pause",
                Description = "Bounded deterministic execution delay in seconds.",
                ValueType = ProcessParameterValueType.WholeNumber
            }
        ],
        OutputArtifactKinds =
        [
            ArtifactKind.Scalar,
            ArtifactKind.Scalar,
            ArtifactKind.Scalar,
            ArtifactKind.Scalar,
            ArtifactKind.Scalar,
            ArtifactKind.Scalar
        ]
    };

    internal static bool IsEnabled(IConfiguration configuration, string? hostEnvironmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.Equals(
                   configuration[ProfileConfigurationKey],
                   ProfileName,
                   StringComparison.Ordinal)
               && configuration.GetValue<bool>(TestInfrastructureConfigurationKey)
               && string.Equals(
                   hostEnvironmentName,
                   "Test",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDefinition(ProcessDefinition definition)
        => string.Equals(definition.ProcessId, ProcessId, StringComparison.Ordinal);

    internal static bool IsJob(ExecutionJobRecord job)
        => job.Spec.Parameters.TryGetValue("protocolProcessId", out var processId)
           && string.Equals(processId, ProcessId, StringComparison.Ordinal);

    internal static OgcProcessDescription CreateDescription(string baseUrl)
        => new()
        {
            Id = ProcessId,
            Title = "CITE Echo",
            Description = "Test-only suite-owned echo fixture for the pinned OGC API Processes ETS. "
                          + "It is absent unless the explicit certification profile is active.",
            Version = "1.0.0",
            JobControlOptions = ImmutableArray.Create("async-execute"),
            OutputTransmission = ImmutableArray.Create("value"),
            Inputs = BuildInputs(),
            Outputs = BuildOutputs(),
            Links = ImmutableArray.Create(
                Link.Create(
                    $"{baseUrl}/ogc/processes/processes/{ProcessId}",
                    RelationTypes.Self,
                    MediaTypes.Json,
                    "This document"),
                Link.Create(
                    $"{baseUrl}/ogc/processes/processes/{ProcessId}/execution",
                    "http://www.opengis.net/def/rel/ogc/1.0/execute",
                    MediaTypes.Json,
                    "Execute process"))
        };

    internal static bool TryAddOutputBindings(
        Dictionary<string, string> metadata,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? requestedOutputs,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        error = null;
        if (requestedOutputs is { Count: > 0 })
        {
            var unknown = requestedOutputs.Keys
                .Where(id => !OutputIds.Contains(id, StringComparer.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (unknown.Length > 0)
            {
                error = $"Unknown CITE echo output(s): {string.Join(", ", unknown)}.";
                return false;
            }

            foreach (var requestedOutput in requestedOutputs)
            {
                if (requestedOutput.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    error = $"CITE echo output '{requestedOutput.Key}' must be an object.";
                    return false;
                }

                if (requestedOutput.Value.TryGetProperty("transmissionMode", out var transmissionMode)
                    && (transmissionMode.ValueKind != System.Text.Json.JsonValueKind.String
                        || !string.Equals(
                            transmissionMode.GetString(),
                            "value",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"CITE echo output '{requestedOutput.Key}' only supports value transmission.";
                    return false;
                }
            }
        }

        var selected = requestedOutputs is { Count: > 0 }
            ? OutputIds.Where(requestedOutputs.ContainsKey)
            : OutputIds;
        var index = 0;
        foreach (var outputId in selected)
        {
            metadata[$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}"] = outputId;
            index++;
        }

        return true;
    }

    internal static bool TryResolveOutputBindings(
        IReadOnlyDictionary<string, string> parameters,
        out ImmutableArray<string> outputIds)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var bindings = parameters
            .Where(entry => entry.Key.StartsWith(
                GeoprocessingProtocolMetadataKeys.OutputNamePrefix,
                StringComparison.Ordinal))
            .ToArray();
        if (bindings.Length is 0 || bindings.Length > OutputIds.Length)
        {
            outputIds = [];
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<string>(bindings.Length);
        for (var index = 0; index < bindings.Length; index++)
        {
            var key = $"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}";
            if (!parameters.TryGetValue(key, out var outputId)
                || !OutputIds.Contains(outputId, StringComparer.Ordinal)
                || builder.Contains(outputId, StringComparer.Ordinal))
            {
                outputIds = [];
                return false;
            }

            builder.Add(outputId);
        }

        outputIds = builder.MoveToImmutable();
        return true;
    }

    private static Dictionary<string, OgcProcessIoDescription> BuildInputs()
        => new(StringComparer.Ordinal)
        {
            ["literal"] = Description("Literal", "Plain string value.", StringSchema()),
            ["object"] = Description("Object", "JSON object value.", ObjectSchema()),
            ["binary"] = Description("Binary", "Inline or referenced GeoTIFF value.", BinarySchema()),
            ["mixed"] = Description("Mixed", "String or object value.", MixedSchema()),
            ["array"] = Description("Array", "Array of string values.", ArraySchema()),
            ["bbox"] = Description("Bounding Box", "OGC bounding-box value.", BoundingBoxSchema()),
            ["pause"] = Description(
                "Pause",
                "Optional deterministic delay in seconds, bounded from zero through ten.",
                new OgcProcessIoSchema { Type = "integer" })
        };

    private static Dictionary<string, OgcProcessIoDescription> BuildOutputs()
        => new(StringComparer.Ordinal)
        {
            ["literal"] = Description("Literal", "Echoed literal value.", StringSchema()),
            ["object"] = Description("Object", "Echoed object value.", ObjectSchema()),
            ["binary"] = Description("Binary", "Echoed binary descriptor.", BinarySchema()),
            ["mixed"] = Description("Mixed", "Echoed mixed value.", MixedSchema()),
            ["array"] = Description("Array", "Echoed array value.", ArraySchema()),
            ["bbox"] = Description(
                "Bounding Box",
                "Echoed OGC bounding-box value.",
                new OgcProcessIoSchema
                {
                    AllOf = ImmutableArray.Create(new OgcProcessIoSchema { Format = "ogc-bbox" })
                })
        };

    private static OgcProcessIoDescription Description(
        string title,
        string description,
        OgcProcessIoSchema schema)
        => new() { Title = title, Description = description, Schema = schema };

    private static OgcProcessIoSchema StringSchema() => new() { Type = "string" };

    private static OgcProcessIoSchema ObjectSchema()
        => new()
        {
            Type = "object",
            Properties = new Dictionary<string, OgcProcessIoSchema>(StringComparer.Ordinal)
            {
                ["value"] = StringSchema()
            }
        };

    private static OgcProcessIoSchema BinarySchema()
        => new()
        {
            Type = "string",
            Format = "byte",
            ContentMediaType = "image/tiff",
            ContentEncoding = "base64"
        };

    private static OgcProcessIoSchema MixedSchema()
        => new()
        {
            OneOf = ImmutableArray.Create(StringSchema(), ObjectSchema())
        };

    private static OgcProcessIoSchema ArraySchema()
        => new() { Type = "array", Items = StringSchema() };

    private static OgcProcessIoSchema BoundingBoxSchema()
        => new()
        {
            Type = "object",
            Properties = new Dictionary<string, OgcProcessIoSchema>(StringComparer.Ordinal)
            {
                ["bbox"] = new OgcProcessIoSchema
                {
                    Type = "array",
                    Items = new OgcProcessIoSchema { Type = "number" }
                },
                ["crs"] = StringSchema()
            }
        };

    private static ProcessParameterSpec Parameter(
        string name,
        string displayName,
        string description,
        bool required = false)
        => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            ValueType = ProcessParameterValueType.Text,
            Required = required
        };
}
