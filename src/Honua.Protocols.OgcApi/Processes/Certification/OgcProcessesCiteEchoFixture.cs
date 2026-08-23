// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
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
    internal const int MaximumPauseSeconds = 10;

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
        IReadOnlyDictionary<string, JsonElement>? requestedOutputs,
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
                if (requestedOutput.Value.ValueKind != JsonValueKind.Object)
                {
                    error = $"CITE echo output '{requestedOutput.Key}' must be an object.";
                    return false;
                }

                if (requestedOutput.Value.TryGetProperty("transmissionMode", out var transmissionMode)
                    && (transmissionMode.ValueKind != JsonValueKind.String
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

    internal static bool TryValidateInputs(
        IReadOnlyDictionary<string, JsonElement>? inputs,
        out string? error)
    {
        error = null;
        if (inputs == null || !inputs.TryGetValue("literal", out var literalInput))
        {
            error = "CITE echo input 'literal' is required.";
            return false;
        }

        if (literalInput.ValueKind != JsonValueKind.String)
        {
            error = "CITE echo input 'literal' must be a string.";
            return false;
        }

        foreach (var input in inputs)
        {
            var valid = input.Key switch
            {
                "literal" => true,
                "object" => TryValidateValueObject(input.Value, "object", out error),
                "binary" => TryValidateBinaryValue(input.Value, out error),
                "mixed" => input.Value.ValueKind == JsonValueKind.String
                           || TryValidateValueObject(input.Value, "mixed", out error),
                "array" => TryValidateStringArray(input.Value, out error),
                "bbox" => TryValidateBoundingBox(input.Value, out error),
                "pause" => TryValidatePause(input.Value, out error),
                _ => false
            };
            if (!valid)
            {
                error ??= $"Unknown CITE echo input '{input.Key}'.";
                return false;
            }
        }

        return true;
    }

    internal static bool TryValidateCanonicalBinaryInput(string? rawBinary, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rawBinary))
        {
            return true;
        }

        // String inputs are stored canonically without their JSON quotes.
        if (IsBase64(rawBinary))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(rawBinary);
            return TryValidateBinaryValue(document.RootElement, out error);
        }
        catch (JsonException)
        {
            error = "CITE echo input 'binary' must be a base64-encoded string "
                    + "or an inline or referenced input object.";
            return false;
        }
    }

    private static bool TryValidateBinaryValue(JsonElement binaryInput, out string? error)
    {
        error = null;
        if (binaryInput.ValueKind == JsonValueKind.String)
        {
            if (IsBase64(binaryInput.GetString()))
            {
                return true;
            }

            error = "CITE echo input 'binary' must be a base64-encoded string.";
            return false;
        }

        if (binaryInput.ValueKind != JsonValueKind.Object)
        {
            error = "CITE echo input 'binary' must be a base64-encoded string "
                    + "or an inline or referenced input object.";
            return false;
        }

        var hasValue = binaryInput.TryGetProperty("value", out var value);
        var hasHref = binaryInput.TryGetProperty("href", out var href);
        if (hasValue == hasHref)
        {
            error = "CITE echo input 'binary' must contain exactly one of 'value' or 'href'.";
            return false;
        }

        if (hasValue)
        {
            if (value.ValueKind != JsonValueKind.String || !IsBase64(value.GetString()))
            {
                error = "CITE echo input 'binary.value' must be a base64-encoded string.";
                return false;
            }
        }
        else if (href.ValueKind != JsonValueKind.String
                 || !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var hrefUri)
                 || (hrefUri.Scheme != Uri.UriSchemeHttp && hrefUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "CITE echo input 'binary.href' must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        if (!binaryInput.TryGetProperty("format", out var format))
        {
            return true;
        }

        if (format.ValueKind != JsonValueKind.Object)
        {
            error = "CITE echo input 'binary.format' must be an object.";
            return false;
        }

        if (format.TryGetProperty("mediaType", out var mediaType)
            && (mediaType.ValueKind != JsonValueKind.String
                || !string.Equals(mediaType.GetString(), "image/tiff", StringComparison.OrdinalIgnoreCase)))
        {
            error = "CITE echo input 'binary.format.mediaType' must be 'image/tiff'.";
            return false;
        }

        if (format.TryGetProperty("encoding", out var encoding)
            && (encoding.ValueKind != JsonValueKind.String
                || !string.Equals(encoding.GetString(), "base64", StringComparison.OrdinalIgnoreCase)))
        {
            error = "CITE echo input 'binary.format.encoding' must be 'base64'.";
            return false;
        }

        return true;
    }

    private static bool TryValidateValueObject(
        JsonElement input,
        string inputName,
        out string? error)
    {
        error = null;
        if (input.ValueKind != JsonValueKind.Object)
        {
            error = $"CITE echo input '{inputName}' must be an object.";
            return false;
        }

        if (input.TryGetProperty("value", out var value)
            && value.ValueKind != JsonValueKind.String)
        {
            error = $"CITE echo input '{inputName}.value' must be a string.";
            return false;
        }

        return true;
    }

    private static bool TryValidateStringArray(JsonElement input, out string? error)
    {
        error = null;
        if (input.ValueKind != JsonValueKind.Array
            || input.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            error = "CITE echo input 'array' must be an array of strings.";
            return false;
        }

        return true;
    }

    private static bool TryValidateBoundingBox(JsonElement input, out string? error)
    {
        error = null;
        if (input.ValueKind != JsonValueKind.Object)
        {
            error = "CITE echo input 'bbox' must be an object.";
            return false;
        }

        if (input.TryGetProperty("bbox", out var bbox)
            && (bbox.ValueKind != JsonValueKind.Array
                || bbox.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Number)))
        {
            error = "CITE echo input 'bbox.bbox' must be an array of numbers.";
            return false;
        }

        if (input.TryGetProperty("crs", out var crs) && crs.ValueKind != JsonValueKind.String)
        {
            error = "CITE echo input 'bbox.crs' must be a string.";
            return false;
        }

        return true;
    }

    private static bool TryValidatePause(JsonElement input, out string? error)
    {
        error = null;
        if (input.ValueKind != JsonValueKind.Number
            || !input.TryGetInt32(out var seconds)
            || seconds < 0
            || seconds > MaximumPauseSeconds)
        {
            error = $"CITE echo input 'pause' must be an integer from 0 through {MaximumPauseSeconds}.";
            return false;
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

    private static bool IsBase64(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length % 4 != 0)
        {
            return false;
        }

        try
        {
            _ = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
