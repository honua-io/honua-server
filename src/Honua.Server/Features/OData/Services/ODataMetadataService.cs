// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData metadata operations.
/// </summary>
internal sealed class ODataMetadataLog;

/// <summary>
/// Service for handling OData metadata and service document operations.
/// Provides dynamic CSDL generation from layer definitions and fallback static metadata.
/// </summary>
internal sealed partial class ODataMetadataService
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly ILogger<ODataMetadataLog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataMetadataService"/> class.
    /// </summary>
    public ODataMetadataService(
        ILayerCatalog layerCatalog,
        ILogger<ODataMetadataLog> logger)
    {
        _layerCatalog = layerCatalog;
        _logger = logger;
    }

    /// <summary>
    /// Generates the OData service document for the given base URL.
    /// </summary>
    public ServiceDocument GenerateServiceDocument(string baseUrl)
    {
        return new ServiceDocument
        {
            Context = $"{baseUrl}/odata/$metadata",
            Value = new[]
            {
                new EntitySet
                {
                    Name = "Layers",
                    Url = "Layers"
                },
                new EntitySet
                {
                    Name = "Features",
                    Url = "Features"
                }
            }
        };
    }

    /// <summary>
    /// Generates the OData metadata document, attempting dynamic generation first with fallback to static.
    /// </summary>
    public async Task<string> GenerateMetadataDocumentAsync(
        IEnumerable<LayerDefinition>? layers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedLayers = layers ?? await _layerCatalog.ListLayersAsync(cancellationToken);
            return GenerateODataMetadata(resolvedLayers.ToArray());
        }
        catch (Exception ex)
        {
            Log.MetadataFallback(_logger, ex);
            // Fall back to static metadata if layer retrieval fails
            return GetStaticMetadata();
        }
    }

    /// <summary>
    /// Generates dynamic OData CSDL metadata from layer definitions.
    /// </summary>
    private static string GenerateODataMetadata(LayerDefinition[] layers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">""");
        sb.AppendLine("  <edmx:DataServices>");
        sb.AppendLine("""    <Schema Namespace="Honua" xmlns="http://docs.oasis-open.org/odata/ns/edm">""");

        // Base Layer entity type
        sb.AppendLine("      <EntityType Name=\"Layer\">");
        sb.AppendLine("        <Key>");
        sb.AppendLine("          <PropertyRef Name=\"Id\"/>");
        sb.AppendLine("        </Key>");
        sb.AppendLine("        <Property Name=\"Id\" Type=\"Edm.Int32\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"Name\" Type=\"Edm.String\"/>");
        sb.AppendLine("        <Property Name=\"Description\" Type=\"Edm.String\"/>");
        sb.AppendLine("        <Property Name=\"GeometryType\" Type=\"Edm.String\"/>");
        sb.AppendLine("      </EntityType>");

        // Base Feature entity type
        sb.AppendLine("      <EntityType Name=\"Feature\">");
        sb.AppendLine("        <Key>");
        sb.AppendLine("          <PropertyRef Name=\"ObjectId\"/>");
        sb.AppendLine("        </Key>");
        sb.AppendLine("        <Property Name=\"ObjectId\" Type=\"Edm.Int64\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"LayerId\" Type=\"Edm.Int32\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"Geometry\" Type=\"Edm.Binary\"/>");
        sb.AppendLine("        <Property Name=\"Attributes\" Type=\"Edm.String\"/>");
        sb.AppendLine("      </EntityType>");

        // Generate specific entity types for each layer with their fields
        foreach (var layer in layers)
        {
            var safeLayerName = SanitizeEntityTypeName(layer.Name);
            sb.AppendLine($"      <EntityType Name=\"{safeLayerName}Feature\" BaseType=\"Honua.Feature\">");

            foreach (var field in layer.AttributeFields)
            {
                var edmType = MapFieldTypeToEdm(field.Type);
                var nullable = field.Nullable ? "true" : "false";
                sb.AppendLine($"        <Property Name=\"{field.Name}\" Type=\"{edmType}\" Nullable=\"{nullable}\"/>");
            }

            sb.AppendLine("      </EntityType>");
        }

        // Entity container with entity sets
        sb.AppendLine("      <EntityContainer Name=\"Container\">");
        sb.AppendLine("        <EntitySet Name=\"Layers\" EntityType=\"Honua.Layer\"/>");
        sb.AppendLine("        <EntitySet Name=\"Features\" EntityType=\"Honua.Feature\"/>");

        // Generate layer-specific entity sets
        foreach (var layer in layers)
        {
            var safeLayerName = SanitizeEntityTypeName(layer.Name);
            sb.AppendLine($"        <EntitySet Name=\"{safeLayerName}\" EntityType=\"Honua.{safeLayerName}Feature\"/>");
        }

        sb.AppendLine("      </EntityContainer>");
        sb.AppendLine("    </Schema>");
        sb.AppendLine("  </edmx:DataServices>");
        sb.AppendLine("</edmx:Edmx>");

        return sb.ToString();
    }

    /// <summary>
    /// Returns static fallback metadata when dynamic generation fails.
    /// </summary>
    private static string GetStaticMetadata()
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
                <edmx:DataServices>
                    <Schema Namespace="Honua" xmlns="http://docs.oasis-open.org/odata/ns/edm">
                        <EntityType Name="Layer">
                            <Key>
                                <PropertyRef Name="Id"/>
                            </Key>
                            <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                            <Property Name="Name" Type="Edm.String"/>
                            <Property Name="Description" Type="Edm.String"/>
                        </EntityType>
                        <EntityType Name="Feature">
                            <Key>
                                <PropertyRef Name="ObjectId"/>
                            </Key>
                            <Property Name="ObjectId" Type="Edm.Int64" Nullable="false"/>
                            <Property Name="LayerId" Type="Edm.Int32" Nullable="false"/>
                            <Property Name="Geometry" Type="Edm.Binary"/>
                            <Property Name="Attributes" Type="Edm.String"/>
                        </EntityType>
                        <EntityContainer Name="Container">
                            <EntitySet Name="Layers" EntityType="Honua.Layer"/>
                            <EntitySet Name="Features" EntityType="Honua.Feature"/>
                        </EntityContainer>
                    </Schema>
                </edmx:DataServices>
            </edmx:Edmx>
            """;
    }

    /// <summary>
    /// Sanitizes a name to be a valid OData entity type name.
    /// Ensures name starts with a letter and contains only alphanumeric characters and underscores.
    /// </summary>
    private static string SanitizeEntityTypeName(string name)
    {
        // Remove invalid characters, ensure starts with letter
        var sb = new StringBuilder();
        var startedWithLetter = false;

        foreach (var c in name)
        {
            if (char.IsLetter(c))
            {
                sb.Append(c);
                startedWithLetter = true;
            }
            else if (startedWithLetter && (char.IsLetterOrDigit(c) || c == '_'))
            {
                sb.Append(c);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "Entity";
    }

    /// <summary>
    /// Maps a FieldType to an OData EDM type.
    /// </summary>
    private static string MapFieldTypeToEdm(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.String => "Edm.String",
            FieldType.Integer => "Edm.Int32",
            FieldType.BigInteger => "Edm.Int64",
            FieldType.Double => "Edm.Double",
            FieldType.Float => "Edm.Single",
            FieldType.Boolean => "Edm.Boolean",
            FieldType.DateTime => "Edm.DateTimeOffset",
            FieldType.Date => "Edm.Date",
            FieldType.Time => "Edm.TimeOfDay",
            FieldType.Geometry => "Edm.Binary",
            FieldType.Json => "Edm.String",
            FieldType.Binary => "Edm.Binary",
            FieldType.Uuid => "Edm.Guid",
            _ => "Edm.String"
        };
    }

    /// <summary>
    /// Logging methods for OData metadata operations.
    /// </summary>
    private static partial class Log
    {
        /// <summary>
        /// Logs when dynamic OData metadata generation fails and falls back to static metadata.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3000, Level = LogLevel.Warning, Message = "Failed to generate dynamic OData metadata, using static metadata.")]
        public static partial void MetadataFallback(ILogger logger, Exception exception);
    }
}
