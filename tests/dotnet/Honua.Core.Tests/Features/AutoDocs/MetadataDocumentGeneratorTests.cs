// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.AutoDocs.Domain;
using Honua.Core.Features.AutoDocs.Services;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.AutoDocs;

/// <summary>
/// Tests verifying that the metadata document generator produces valid
/// ISO 19115 XML, FGDC XML, and data dictionary output.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class MetadataDocumentGeneratorTests
{
    private readonly MetadataDocumentGenerator _generator = new();
    private readonly MetadataDocumentRequest _request;

    public MetadataDocumentGeneratorTests()
    {
        var layer = new LayerDefinition(
            Id: 1,
            Name: "land_parcels",
            Description: "Municipal land parcel boundaries with zoning and assessment data",
            GeometryType: GeometryType.Polygon,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false, Description: "Unique identifier"),
                new FieldDefinition("parcel_id", FieldType.String, Length: 20, Description: "Parcel identification number"),
                new FieldDefinition("zoning", FieldType.String, Length: 10, Description: "Zoning classification code"),
                new FieldDefinition("assessed_value", FieldType.Double, Description: "Assessed property value in USD"),
                new FieldDefinition("shape", FieldType.Geometry),
            ]);

        _request = new MetadataDocumentRequest(
            Layer: layer,
            ServiceName: "CityGIS",
            OrganizationName: "City of Portland",
            ContactEmail: "gis@portland.gov",
            Abstract: "Land parcel boundaries for Portland, Oregon",
            Purpose: "Planning, zoning, and assessment management",
            Keywords: ["parcels", "zoning", "property", "assessment"],
            AccessConstraints: "Public domain");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_ProducesValidIso19115Xml()
    {
        var result = _generator.Generate(_request);

        result.Iso19115Xml.Should().NotBeNullOrWhiteSpace();

        // Verify it's valid XML
        var doc = XDocument.Parse(result.Iso19115Xml);
        doc.Should().NotBeNull();

        // Check root element
        XNamespace gmd = "http://www.isotc211.org/2005/gmd";
        doc.Root!.Name.Should().Be(gmd + "MD_Metadata");

        // Check file identifier
        var fileId = doc.Descendants(gmd + "fileIdentifier").FirstOrDefault();
        fileId.Should().NotBeNull();

        // Check reference system contains EPSG code
        var refSys = doc.Descendants(gmd + "referenceSystemInfo").FirstOrDefault();
        refSys.Should().NotBeNull();
        refSys!.ToString().Should().Contain("EPSG:4326");

        // Check identification info
        var identInfo = doc.Descendants(gmd + "MD_DataIdentification").FirstOrDefault();
        identInfo.Should().NotBeNull();

        // Check title
        var title = doc.Descendants(gmd + "title").FirstOrDefault();
        title.Should().NotBeNull();
        title!.Value.Should().Contain("land_parcels");

        // Check keywords
        var keywords = doc.Descendants(gmd + "keyword").ToList();
        keywords.Should().HaveCount(4);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_ProducesValidFgdcXml()
    {
        var result = _generator.Generate(_request);

        result.FgdcXml.Should().NotBeNullOrWhiteSpace();

        // Verify it's valid XML
        var doc = XDocument.Parse(result.FgdcXml);
        doc.Should().NotBeNull();

        // Check root element
        doc.Root!.Name.LocalName.Should().Be("metadata");

        // Check title
        var title = doc.Descendants("title").FirstOrDefault();
        title.Should().NotBeNull();
        title!.Value.Should().Be("land_parcels");

        // Check abstract
        var abstract_ = doc.Descendants("abstract").FirstOrDefault();
        abstract_.Should().NotBeNull();
        abstract_!.Value.Should().Contain("Land parcel boundaries");

        // Check FGDC standard reference
        var metstdn = doc.Descendants("metstdn").FirstOrDefault();
        metstdn.Should().NotBeNull();
        metstdn!.Value.Should().Contain("FGDC");

        // Check entity/attribute info
        var attrs = doc.Descendants("attr").ToList();
        attrs.Should().HaveCount(4); // objectid, parcel_id, zoning, assessed_value (no geometry)

        // Check spatial reference
        var geogcsn = doc.Descendants("geogcsn").FirstOrDefault();
        geogcsn.Should().NotBeNull();
        geogcsn!.Value.Should().Contain("EPSG:4326");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_ProducesDataDictionary()
    {
        var result = _generator.Generate(_request);

        result.DataDictionary.Should().NotBeNullOrWhiteSpace();
        result.DataDictionary.Should().Contain("# Data Dictionary: land_parcels");
        result.DataDictionary.Should().Contain("EPSG:4326");
        result.DataDictionary.Should().Contain("City of Portland");
        result.DataDictionary.Should().Contain("| Field Name |");
        result.DataDictionary.Should().Contain("| parcel_id |");
        result.DataDictionary.Should().Contain("| zoning |");
        result.DataDictionary.Should().Contain("| assessed_value |");
        result.DataDictionary.Should().Contain("## Geometry");
        result.DataDictionary.Should().Contain("## Keywords");
        result.DataDictionary.Should().Contain("parcels");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_MinimalRequest_ProducesValidOutput()
    {
        var minimalLayer = LayerDefinition.CreateBasic(1, "test", GeometryType.Point);
        var request = new MetadataDocumentRequest(
            Layer: minimalLayer,
            ServiceName: "TestService");

        var result = _generator.Generate(request);

        result.Iso19115Xml.Should().NotBeNullOrWhiteSpace();
        result.FgdcXml.Should().NotBeNullOrWhiteSpace();
        result.DataDictionary.Should().NotBeNullOrWhiteSpace();

        // Both must parse as valid XML
        var isoDoc = XDocument.Parse(result.Iso19115Xml);
        isoDoc.Should().NotBeNull();

        var fgdcDoc = XDocument.Parse(result.FgdcXml);
        fgdcDoc.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_DataDictionary_ExcludesGeometryField()
    {
        var result = _generator.Generate(_request);

        result.DataDictionary.Should().NotContain("| shape |");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_SetsGeneratedAtTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var result = _generator.Generate(_request);
        var after = DateTimeOffset.UtcNow;

        result.GeneratedAt.Should().BeOnOrAfter(before);
        result.GeneratedAt.Should().BeOnOrBefore(after);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_ProjectedCrs_OmitsGeographicBoundingBox()
    {
        var projectedLayer = new LayerDefinition(
            Id: 1,
            Name: "buildings_3857",
            Description: "Buildings in Web Mercator",
            GeometryType: GeometryType.Polygon,
            SpatialReference: SpatialReference.WebMercator,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("shape", FieldType.Geometry),
            ],
            Extent: new FeatureExtent
            {
                MinX = -13656000,
                MinY = 5700000,
                MaxX = -13654000,
                MaxY = 5702000,
                SpatialReference = 3857,
            });

        var request = new MetadataDocumentRequest(
            Layer: projectedLayer,
            ServiceName: "TestService");

        var result = _generator.Generate(request);

        // ISO 19115: EX_GeographicBoundingBox must not appear for non-WGS84 extents
        XNamespace gmd = "http://www.isotc211.org/2005/gmd";
        var isoDoc = XDocument.Parse(result.Iso19115Xml);
        isoDoc.Descendants(gmd + "EX_GeographicBoundingBox").Should().BeEmpty(
            "projected coordinates must not be emitted as EX_GeographicBoundingBox per ISO 19115 B.3.1.2");

        // Reference system should still be present
        isoDoc.Descendants(gmd + "referenceSystemInfo").Should().NotBeEmpty();
        isoDoc.ToString().Should().Contain("EPSG:3857");

        // FGDC: <bounding> must not appear for non-WGS84 extents per FGDC-STD-001-1998 §1.5.1.2
        var fgdcDoc = XDocument.Parse(result.FgdcXml);
        fgdcDoc.Descendants("bounding").Should().BeEmpty(
            "projected coordinates must not be emitted as FGDC bounding per FGDC-STD-001-1998 §1.5.1.2");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Generate_GeographicNad83_EmitsBoundingBox()
    {
        var nad83Layer = new LayerDefinition(
            Id: 1,
            Name: "parcels_nad83",
            Description: "Parcels in NAD83",
            GeometryType: GeometryType.Polygon,
            SpatialReference: SpatialReference.Create(4269),
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("shape", FieldType.Geometry),
            ],
            Extent: new FeatureExtent
            {
                MinX = -122.7,
                MinY = 45.4,
                MaxX = -122.5,
                MaxY = 45.6,
                SpatialReference = 4269,
            });

        var request = new MetadataDocumentRequest(
            Layer: nad83Layer,
            ServiceName: "TestService");

        var result = _generator.Generate(request);

        // ISO 19115: geographic CRS extents must be emitted
        XNamespace gmd = "http://www.isotc211.org/2005/gmd";
        var isoDoc = XDocument.Parse(result.Iso19115Xml);
        isoDoc.Descendants(gmd + "EX_GeographicBoundingBox").Should().NotBeEmpty(
            "NAD83 (EPSG:4269) is a geographic CRS — bounding box should be emitted");

        // FGDC: geographic CRS extents must be emitted
        var fgdcDoc = XDocument.Parse(result.FgdcXml);
        fgdcDoc.Descendants("bounding").Should().NotBeEmpty(
            "NAD83 (EPSG:4269) is a geographic CRS — FGDC bounding should be emitted");

        // Reference system should reflect NAD83
        isoDoc.ToString().Should().Contain("EPSG:4269");
    }
}
