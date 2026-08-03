// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster.Functions;

/// <summary>Source-generated JSON contract for canonical raster-function graphs.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RasterFunctionDefinition))]
[JsonSerializable(typeof(RasterFunctionInvocation))]
[JsonSerializable(typeof(RasterFunctionNode))]
[JsonSerializable(typeof(RasterFunctionInputNode))]
[JsonSerializable(typeof(RasterFunctionIdentityNode))]
[JsonSerializable(typeof(RasterFunctionBandSelectNode))]
[JsonSerializable(typeof(RasterFunctionSpectralIndexNode))]
[JsonSerializable(typeof(RasterFunctionClipNode))]
[JsonSerializable(typeof(RasterFunctionResampleNode))]
[JsonSerializable(typeof(RasterFunctionReprojectNode))]
[JsonSerializable(typeof(RasterFunctionStretchNode))]
[JsonSerializable(typeof(RasterFunctionColormapNode))]
[JsonSerializable(typeof(RasterFunctionTerrainNode))]
[JsonSerializable(typeof(RasterFunctionReclassifyNode))]
[JsonSerializable(typeof(RasterFunctionCompositeNode))]
[JsonSerializable(typeof(RasterReclassificationRule))]
[JsonSerializable(typeof(Dictionary<string, RasterSourceDescriptor>))]
public sealed partial class RasterFunctionJsonContext : JsonSerializerContext;

/// <summary>AOT-safe serialization and deterministic identity helpers.</summary>
public static class RasterFunctionJson
{
    /// <summary>Serializes a graph through source-generated metadata.</summary>
    public static string Serialize(RasterFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, RasterFunctionJsonContext.Default.RasterFunctionDefinition);
    }

    /// <summary>Deserializes a graph through source-generated metadata.</summary>
    /// <exception cref="JsonException">The graph is malformed or carries an unknown node type.</exception>
    public static RasterFunctionDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RasterFunctionJsonContext.Default.RasterFunctionDefinition)
            ?? throw new JsonException("Raster function definition cannot be null.");
    }

    /// <summary>
    /// Serializes a definition in deterministic node-id order. Input ordering is retained because
    /// it is semantically significant; definition list order is not.
    /// </summary>
    public static string Normalize(RasterFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var canonical = definition with
        {
            Nodes = definition.Nodes
                .OrderBy(static node => node.Id, StringComparer.Ordinal)
                .ToArray(),
        };
        return Serialize(canonical);
    }

    /// <summary>Computes a lower-case SHA-256 identity over normalized graph JSON.</summary>
    public static string ComputeSha256(RasterFunctionDefinition definition)
    {
        var normalized = Normalize(definition);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
