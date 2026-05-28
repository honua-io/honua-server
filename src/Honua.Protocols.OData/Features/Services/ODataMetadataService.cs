// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.OData.Models;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Log category for OData metadata operations.
/// </summary>
internal sealed class ODataMetadataLog;

/// <summary>
/// Service for handling OData metadata and service document operations.
/// Provides dynamic CSDL generation from Metadata v2 canonical resources and fallback static metadata.
/// </summary>
internal sealed partial class ODataMetadataService
{
    private readonly ILogger<ODataMetadataLog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataMetadataService"/> class.
    /// </summary>
    public ODataMetadataService(ILogger<ODataMetadataLog> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates the OData service document for the given base URL.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as an instance method to preserve the current service API shape.")]
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
                    Kind = "EntitySet",
                    Url = "Layers"
                },
                new EntitySet
                {
                    Name = "Features",
                    Kind = "EntitySet",
                    Url = "Features"
                }
            }
        };
    }

    /// <summary>
    /// Generates the OData CSDL metadata document directly from a sequence of canonical
    /// V2 resources. Falls back to the static metadata document if generation fails for
    /// any reason.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as an instance method to preserve the service API shape.")]
    public string GenerateMetadataDocumentV2(IEnumerable<MetadataV2Resource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        try
        {
            return GenerateODataMetadataV2(resources.ToArray());
        }
        catch (Exception ex)
        {
            Log.MetadataFallback(_logger, ex);
            return GetStaticMetadata();
        }
    }

    /// <summary>
    /// Returns static fallback metadata when dynamic generation fails.
    /// </summary>
    private static string GetStaticMetadata()
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <edmx:Edmx Version="4.01" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
                <edmx:Reference Uri="http://vocabs.odata.org/capabilities/v1">
                    <edmx:Include Namespace="Org.OData.Capabilities.V1" Alias="Capabilities"/>
                </edmx:Reference>
                <edmx:DataServices>
                    <Schema Namespace="Honua" xmlns="http://docs.oasis-open.org/odata/ns/edm">
                        <EntityType Name="Layer">
                            <Key>
                                <PropertyRef Name="Id"/>
                            </Key>
                            <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                            <Property Name="Name" Type="Edm.String"/>
                            <Property Name="Description" Type="Edm.String"/>
                            <Property Name="GeometryType" Type="Edm.String"/>
                            <Property Name="Srid" Type="Edm.Int32"/>
                            <NavigationProperty Name="Features" Type="Collection(Honua.Feature)"/>
                        </EntityType>
                        <EntityType Name="Feature" OpenType="true">
                            <Key>
                                <PropertyRef Name="LayerId"/>
                                <PropertyRef Name="ObjectId"/>
                            </Key>
                            <Property Name="LayerId" Type="Edm.Int32" Nullable="false"/>
                            <Property Name="ObjectId" Type="Edm.Int64" Nullable="false"/>
                            <Property Name="Geometry" Type="Edm.Geometry" SRID="variable"/>
                        </EntityType>
                        <EntityContainer Name="Container">
                            <EntitySet Name="Layers" EntityType="Honua.Layer">
                                <NavigationPropertyBinding Path="Features" Target="Features"/>
                            </EntitySet>
                            <EntitySet Name="Features" EntityType="Honua.Feature"/>
                        </EntityContainer>
                        <Annotations Target="Honua.Container/Features">
                            <Annotation Term="Capabilities.FilterRestrictions">
                                <Record>
                                    <PropertyValue Property="Filterable" Bool="true"/>
                                    <PropertyValue Property="RequiresFilter" Bool="true"/>
                                    <PropertyValue Property="RequiredProperties">
                                        <Collection>
                                            <PropertyPath>LayerId</PropertyPath>
                                        </Collection>
                                    </PropertyValue>
                                </Record>
                            </Annotation>
                            <Annotation Term="Capabilities.SearchRestrictions">
                                <Record>
                                    <PropertyValue Property="Searchable" Bool="true"/>
                                </Record>
                            </Annotation>
                            <Annotation Term="Capabilities.ExpandRestrictions">
                                <Record>
                                    <PropertyValue Property="Expandable" Bool="true"/>
                                </Record>
                            </Annotation>
                            <Annotation Term="Capabilities.ChangeTracking">
                                <Record>
                                    <PropertyValue Property="Supported" Bool="true"/>
                                </Record>
                            </Annotation>
                        </Annotations>
                    </Schema>
                </edmx:DataServices>
            </edmx:Edmx>
            """;
    }

    private static string GenerateODataMetadataV2(MetadataV2Resource[] resources)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<edmx:Edmx Version="4.01" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">""");
        sb.AppendLine("""  <edmx:Reference Uri="http://vocabs.odata.org/capabilities/v1">""");
        sb.AppendLine("""    <edmx:Include Namespace="Org.OData.Capabilities.V1" Alias="Capabilities"/>""");
        sb.AppendLine("""  </edmx:Reference>""");
        sb.AppendLine("  <edmx:DataServices>");
        sb.AppendLine("""    <Schema Namespace="Honua" xmlns="http://docs.oasis-open.org/odata/ns/edm">""");

        var (geometryEdmType, geometrySridAttribute) = ResolveGeometryEdmTypeV2(resources);
        var relationshipNames = CollectRelationshipNamesV2(resources);

        sb.AppendLine("      <EntityType Name=\"Layer\">");
        sb.AppendLine("        <Key>");
        sb.AppendLine("          <PropertyRef Name=\"Id\"/>");
        sb.AppendLine("        </Key>");
        sb.AppendLine("        <Property Name=\"Id\" Type=\"Edm.Int32\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"Name\" Type=\"Edm.String\"/>");
        sb.AppendLine("        <Property Name=\"Description\" Type=\"Edm.String\"/>");
        sb.AppendLine("        <Property Name=\"GeometryType\" Type=\"Edm.String\"/>");
        sb.AppendLine("        <Property Name=\"Srid\" Type=\"Edm.Int32\"/>");
        sb.AppendLine("        <NavigationProperty Name=\"Features\" Type=\"Collection(Honua.Feature)\"/>");
        sb.AppendLine("      </EntityType>");

        sb.AppendLine("      <EntityType Name=\"Feature\" OpenType=\"true\">");
        sb.AppendLine("        <Key>");
        sb.AppendLine("          <PropertyRef Name=\"LayerId\"/>");
        sb.AppendLine("          <PropertyRef Name=\"ObjectId\"/>");
        sb.AppendLine("        </Key>");
        sb.AppendLine("        <Property Name=\"LayerId\" Type=\"Edm.Int32\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"ObjectId\" Type=\"Edm.Int64\" Nullable=\"false\"/>");
        sb.AppendLine($"        <Property Name=\"Geometry\" Type=\"{geometryEdmType}\"{geometrySridAttribute}/>");
        foreach (var relationshipName in relationshipNames)
        {
            sb.AppendLine($"        <NavigationProperty Name=\"{relationshipName}\" Type=\"Collection(Honua.Feature)\"/>");
        }
        sb.AppendLine("      </EntityType>");

        sb.AppendLine("      <EntityContainer Name=\"Container\">");
        sb.AppendLine("        <EntitySet Name=\"Features\" EntityType=\"Honua.Feature\">");
        foreach (var relationshipName in relationshipNames)
        {
            sb.AppendLine($"          <NavigationPropertyBinding Path=\"{relationshipName}\" Target=\"Features\"/>");
        }
        sb.AppendLine("        </EntitySet>");
        sb.AppendLine("        <EntitySet Name=\"Layers\" EntityType=\"Honua.Layer\">");
        sb.AppendLine("          <NavigationPropertyBinding Path=\"Features\" Target=\"Features\"/>");
        sb.AppendLine("        </EntitySet>");
        sb.AppendLine("      </EntityContainer>");
        sb.AppendLine("      <Annotations Target=\"Honua.Container/Features\">");
        sb.AppendLine("        <Annotation Term=\"Capabilities.FilterRestrictions\">");
        sb.AppendLine("          <Record>");
        sb.AppendLine("            <PropertyValue Property=\"Filterable\" Bool=\"true\"/>");
        sb.AppendLine("            <PropertyValue Property=\"RequiresFilter\" Bool=\"true\"/>");
        sb.AppendLine("            <PropertyValue Property=\"RequiredProperties\">");
        sb.AppendLine("              <Collection>");
        sb.AppendLine("                <PropertyPath>LayerId</PropertyPath>");
        sb.AppendLine("              </Collection>");
        sb.AppendLine("            </PropertyValue>");
        sb.AppendLine("          </Record>");
        sb.AppendLine("        </Annotation>");
        sb.AppendLine("        <Annotation Term=\"Capabilities.SearchRestrictions\">");
        sb.AppendLine("          <Record>");
        sb.AppendLine("            <PropertyValue Property=\"Searchable\" Bool=\"true\"/>");
        sb.AppendLine("          </Record>");
        sb.AppendLine("        </Annotation>");
        sb.AppendLine("        <Annotation Term=\"Capabilities.ExpandRestrictions\">");
        sb.AppendLine("          <Record>");
        sb.AppendLine("            <PropertyValue Property=\"Expandable\" Bool=\"true\"/>");
        sb.AppendLine("          </Record>");
        sb.AppendLine("        </Annotation>");
        sb.AppendLine("        <Annotation Term=\"Capabilities.ChangeTracking\">");
        sb.AppendLine("          <Record>");
        sb.AppendLine("            <PropertyValue Property=\"Supported\" Bool=\"true\"/>");
        sb.AppendLine("          </Record>");
        sb.AppendLine("        </Annotation>");
        sb.AppendLine("      </Annotations>");
        sb.AppendLine("      <Annotations Target=\"Honua.Container/Layers\">");
        sb.AppendLine("        <Annotation Term=\"Capabilities.FilterRestrictions\">");
        sb.AppendLine("          <Record>");
        sb.AppendLine("            <PropertyValue Property=\"Filterable\" Bool=\"true\"/>");
        sb.AppendLine("          </Record>");
        sb.AppendLine("        </Annotation>");
        sb.AppendLine("      </Annotations>");
        sb.AppendLine("    </Schema>");
        sb.AppendLine("  </edmx:DataServices>");
        sb.AppendLine("</edmx:Edmx>");

        return sb.ToString();
    }

    private static (string GeometryEdmType, string SridAttribute) ResolveGeometryEdmTypeV2(
        MetadataV2Resource[] resources)
    {
        if (resources.Length == 0)
        {
            return ("Edm.Geography", " SRID=\"4326\"");
        }

        var distinctSrids = resources
            .Select(r => r.ReadSrid())
            .Where(srid => srid.HasValue)
            .Select(srid => srid!.Value)
            .Distinct()
            .ToArray();
        if (distinctSrids.Length == 1)
        {
            var srid = distinctSrids[0];
            var geometryEdmType = srid == SpatialReference.WGS84.Wkid ? "Edm.Geography" : "Edm.Geometry";
            var sridAttribute = $" SRID=\"{srid}\"";
            return (geometryEdmType, sridAttribute);
        }

        return ("Edm.Geometry", " SRID=\"variable\"");
    }

    private static string[] CollectRelationshipNamesV2(MetadataV2Resource[] resources)
    {
        var relationships = resources
            .SelectMany(r => r.Relationships)
            .Select(relationship => new
            {
                RelationshipId = relationship.Id,
                SanitizedName = ODataUtilityService.SanitizeIdentifier(relationship.Name)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SanitizedName))
            .ToArray();

        var duplicateNames = relationships
            .GroupBy(entry => entry.SanitizedName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in relationships)
        {
            var name = duplicateNames.Contains(relationship.SanitizedName)
                ? ODataUtilityService.BuildRelationshipMetadataNameForV2(
                    relationship.SanitizedName,
                    relationship.RelationshipId)
                : relationship.SanitizedName;

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
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
