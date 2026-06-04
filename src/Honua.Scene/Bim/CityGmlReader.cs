// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Bim;

/// <summary>
/// Pure-managed reader for the CityGML building subset (#1207). Parses
/// <c>bldg:Building</c> features, their boundary surfaces (<c>WallSurface</c> /
/// <c>RoofSurface</c> / <c>GroundSurface</c> / ...) and the generic/CityGML
/// attributes attached at the building and surface levels into a
/// <see cref="CityGmlModel"/> carrying Building Scene Layer semantics.
/// </summary>
/// <remarks>
/// <para>
/// The reader is namespace-tolerant: it matches on XML local names so a
/// document may use either CityGML 1.0 (<c>http://www.opengis.net/citygml/1.0</c>)
/// or 2.0 (<c>http://www.opengis.net/citygml/2.0</c>) namespace prefixes, and it
/// reads <c>gml:posList</c> as well as the older <c>gml:pos</c> coordinate
/// encoding. Geometry is taken from the boundary surfaces' exterior
/// <c>gml:LinearRing</c>s at any LOD; inner rings are skipped (a documented v1
/// limitation).
/// </para>
/// <para>
/// The reader does not transform CRS; the BSL builder takes a caller-supplied
/// projection delegate so a projected-CRS document can be placed on the
/// ellipsoid by the tiling stage, mirroring the point-cloud pipeline. Output is
/// deterministic: features keep document order. Parsing is hardened against XXE
/// (no DTD processing, no external entity resolution).
/// </para>
/// </remarks>
public static class CityGmlReader
{
    /// <summary>
    /// Upper bound on the total number of characters the XML reader will accept
    /// from a single CityGML document. Caps memory amplification from an
    /// oversized-but-well-formed document (the whole tree is materialized by
    /// <see cref="XDocument"/>). 256 MiB of characters comfortably exceeds any
    /// realistic single-building-tile CityGML payload while bounding the parse.
    /// </summary>
    private const long MaxDocumentCharacters = 256L * 1024 * 1024;

    /// <summary>
    /// Parses a CityGML document from a UTF-8 byte buffer into a
    /// <see cref="CityGmlModel"/>.
    /// </summary>
    /// <param name="source">The CityGML document bytes (UTF-8 or BOM-prefixed).</param>
    /// <returns>The parsed building model with BSL hierarchy and attributes.</returns>
    /// <exception cref="CityGmlFormatException">The document is malformed or contains no parseable building geometry.</exception>
    public static CityGmlModel Read(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var stream = new MemoryStream(source, writable: false);
        return Read(stream);
    }

    /// <summary>
    /// Parses a CityGML document from a stream into a <see cref="CityGmlModel"/>.
    /// </summary>
    /// <param name="source">The CityGML document stream.</param>
    /// <returns>The parsed building model with BSL hierarchy and attributes.</returns>
    /// <exception cref="CityGmlFormatException">The document is malformed or contains no parseable building geometry.</exception>
    public static CityGmlModel Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        XDocument document;
        try
        {
            // LoadOptions.None + the safe reader settings (DTD disabled, no
            // external resolver) harden the parse against XXE; CityGML never
            // relies on a DTD. MaxCharactersFromEntities = 0 blocks entity-
            // expansion amplification and MaxCharactersInDocument caps the total
            // document size so an oversized-but-well-formed document cannot make
            // XDocument materialize an unbounded tree (DoS). The shared
            // SecureXmlDocumentParser applies the same DTD/entity caps but only
            // accepts a buffered string; CityGML reads a stream, so the caps are
            // applied inline here to avoid buffering the whole document twice.
            using var reader = System.Xml.XmlReader.Create(source, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaxDocumentCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            document = XDocument.Load(reader);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CityGmlFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                "CityGML document is not well-formed XML.",
                ex);
        }

        var root = document.Root
            ?? throw new CityGmlFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                "CityGML document is empty.");

        var sourceCrs = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Envelope")?
            .Attribute("srsName")?.Value;
        var latitudeFirst = IsLatitudeFirst(sourceCrs);

        var buildings = new List<CityGmlBuilding>();
        // Document-order ordinal for the synthetic fallback id; incremented for
        // every Building/BuildingPart element in document order so a missing
        // gml:id yields a deterministic id ("building-{ordinal}") that is stable
        // across reads, preserving the documented byte-stability guarantee
        // (a random Guid would break content-addressed caching / ETags).
        var buildingOrdinal = 0;
        foreach (var element in root.Descendants())
        {
            if (element.Name.LocalName is not ("Building" or "BuildingPart"))
            {
                continue;
            }

            var building = ReadBuilding(element, buildingOrdinal);
            buildingOrdinal++;
            if (building is not null)
            {
                buildings.Add(building);
            }
        }

        if (buildings.Count == 0)
        {
            throw new CityGmlFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                "CityGML document contains no parseable bldg:Building geometry.");
        }

        return new CityGmlModel
        {
            SourceCrs = sourceCrs,
            LatitudeFirst = latitudeFirst,
            Buildings = buildings
        };
    }

    private static CityGmlBuilding? ReadBuilding(XElement element, int ordinal)
    {
        var id = GmlId(element) ?? $"building-{ordinal.ToString(CultureInfo.InvariantCulture)}";

        string? name = null;
        int? storeysAbove = null;
        int? storeysBelow = null;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        // Direct (non-descendant) children give the building-level metadata; a
        // nested BuildingPart's own surfaces are handled when the descendant
        // walk reaches that BuildingPart element separately.
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "name" when name is null:
                    name = Trim(child.Value);
                    break;
                case "storeysAboveGround":
                    storeysAbove = ParseInt(child.Value);
                    break;
                case "storeysBelowGround":
                    storeysBelow = ParseInt(child.Value);
                    break;
                case "stringAttribute":
                case "intAttribute":
                case "doubleAttribute":
                case "genericAttribute":
                    ReadGenericAttribute(child, attributes);
                    break;
            }
        }

        var surfaces = new List<CityGmlBoundarySurface>();
        // Collect boundary surfaces owned by THIS building only — stop the walk
        // at any nested Building/BuildingPart so a part's surfaces are not
        // double-counted (the descendant pass over the document handles parts).
        foreach (var surfaceElement in DescendantsUntilNestedBuilding(element))
        {
            if (IsBoundarySurfaceType(surfaceElement.Name.LocalName))
            {
                ReadBoundarySurface(surfaceElement, surfaces);
            }
        }

        if (surfaces.Count == 0)
        {
            return null;
        }

        // CityGML 2.0 has no first-class storey; synthesise a single
        // whole-building storey so the BSL hierarchy stays building -> storey ->
        // component. Storey decomposition from bldg:Room is a tracked follow-up.
        var storey = new CityGmlStorey
        {
            Id = $"{id}-storey-1",
            Name = storeysAbove is { } above ? $"Storeys above ground: {above}" : null,
            Surfaces = surfaces
        };

        return new CityGmlBuilding
        {
            Id = id,
            Name = name,
            StoreysAboveGround = storeysAbove,
            StoreysBelowGround = storeysBelow,
            Attributes = attributes,
            Storeys = new[] { storey }
        };
    }

    /// <summary>
    /// Yields descendants of <paramref name="building"/> without crossing into a
    /// nested <c>Building</c>/<c>BuildingPart</c> subtree (those are processed by
    /// the top-level descendant walk so their surfaces are attributed to the
    /// correct owner).
    /// </summary>
    private static IEnumerable<XElement> DescendantsUntilNestedBuilding(XElement building)
    {
        foreach (var child in building.Elements())
        {
            if (child.Name.LocalName is "Building" or "BuildingPart")
            {
                continue;
            }
            yield return child;
            foreach (var nested in DescendantsUntilNestedBuilding(child))
            {
                yield return nested;
            }
        }
    }

    private static void ReadBoundarySurface(XElement element, List<CityGmlBoundarySurface> sink)
    {
        var surfaceType = element.Name.LocalName;
        var discipline = MapDiscipline(surfaceType);
        var baseId = GmlId(element) ?? $"{surfaceType}-{sink.Count}";

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName is "stringAttribute" or "intAttribute"
                or "doubleAttribute" or "genericAttribute")
            {
                ReadGenericAttribute(child, attributes);
            }
        }

        // Each exterior LinearRing under the surface becomes one BSL component
        // feature. gml:exterior selects the outer ring; interior holes are
        // skipped in this slice.
        var ringIndex = 0;
        foreach (var ringElement in element.Descendants().Where(e => e.Name.LocalName == "LinearRing"))
        {
            if (IsInteriorRing(ringElement))
            {
                continue;
            }

            var ring = ReadLinearRing(ringElement);
            if (ring.Count < 3)
            {
                continue;
            }

            sink.Add(new CityGmlBoundarySurface
            {
                Id = ringIndex == 0 ? baseId : $"{baseId}-{ringIndex}",
                SurfaceType = surfaceType,
                Discipline = discipline,
                Attributes = attributes,
                Ring = ring
            });
            ringIndex++;
        }
    }

    private static bool IsInteriorRing(XElement ring)
    {
        // A LinearRing nested under a gml:interior is a hole; skip it.
        for (var parent = ring.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Name.LocalName == "interior")
            {
                return true;
            }
            if (parent.Name.LocalName == "exterior" || parent.Name.LocalName == "Polygon")
            {
                return false;
            }
        }
        return false;
    }

    private static List<CityGmlVertex> ReadLinearRing(XElement ring)
    {
        var vertices = new List<CityGmlVertex>();
        foreach (var child in ring.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "posList":
                    var dim = ParseInt(child.Attribute("srsDimension")?.Value) ?? 3;
                    AppendPosList(child.Value, dim, vertices);
                    break;
                case "pos":
                    AppendPos(child.Value, vertices);
                    break;
            }
        }
        return vertices;
    }

    private static void AppendPosList(string? text, int dimension, List<CityGmlVertex> sink)
    {
        if (string.IsNullOrWhiteSpace(text) || dimension < 2)
        {
            return;
        }

        var tokens = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i + dimension - 1 < tokens.Length; i += dimension)
        {
            if (!TryParseDouble(tokens[i], out var x) || !TryParseDouble(tokens[i + 1], out var y))
            {
                continue;
            }
            var z = 0.0;
            if (dimension >= 3)
            {
                _ = TryParseDouble(tokens[i + 2], out z);
            }
            sink.Add(new CityGmlVertex(x, y, z));
        }
    }

    private static void AppendPos(string? text, List<CityGmlVertex> sink)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var tokens = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2
            || !TryParseDouble(tokens[0], out var x)
            || !TryParseDouble(tokens[1], out var y))
        {
            return;
        }
        var z = 0.0;
        if (tokens.Length >= 3)
        {
            _ = TryParseDouble(tokens[2], out z);
        }
        sink.Add(new CityGmlVertex(x, y, z));
    }

    private static void ReadGenericAttribute(XElement element, Dictionary<string, string> sink)
    {
        var name = element.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        // CityGML generic attributes carry their value either as a nested
        // <gen:value> element (1.0 style) or as direct text content (the common
        // gen:stringAttribute form). Prefer the nested value when present.
        var valueElement = element.Elements().FirstOrDefault(e => e.Name.LocalName == "value");
        var value = valueElement is not null ? Trim(valueElement.Value) : Trim(element.Value);
        if (!string.IsNullOrEmpty(value))
        {
            sink[name] = value;
        }
    }

    /// <summary>
    /// Maps a CityGML surface type to a BIM discipline / Building Scene Layer
    /// sub-layer. Architectural envelope surfaces map to <c>Architectural</c>;
    /// load-bearing slabs (ground/floor/ceiling) map to <c>Structural</c>.
    /// </summary>
    private static string MapDiscipline(string surfaceType) => surfaceType switch
    {
        "GroundSurface" => "Structural",
        "FloorSurface" => "Structural",
        "CeilingSurface" => "Structural",
        "OuterFloorSurface" => "Structural",
        "OuterCeilingSurface" => "Structural",
        _ => "Architectural"
    };

    private static bool IsBoundarySurfaceType(string localName) => localName switch
    {
        "WallSurface" => true,
        "RoofSurface" => true,
        "GroundSurface" => true,
        "ClosureSurface" => true,
        "OuterCeilingSurface" => true,
        "OuterFloorSurface" => true,
        "CeilingSurface" => true,
        "FloorSurface" => true,
        "InteriorWallSurface" => true,
        _ => false
    };

    private static string? GmlId(XElement element)
        => element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;

    private static bool IsLatitudeFirst(string? srsName)
    {
        if (string.IsNullOrEmpty(srsName))
        {
            return false;
        }

        // EPSG geographic CRS (4326/4979) declare latitude-first axis order in
        // their authoritative definition; many CityGML producers honor that for
        // urn-form srsName. EPSG:4326 short form and projected CRS stay
        // longitude/easting-first.
        return srsName.Contains("EPSG::4326", StringComparison.OrdinalIgnoreCase)
            || srsName.Contains("EPSG::4979", StringComparison.OrdinalIgnoreCase)
            || srsName.Contains("EPSG:4979", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value) => value.Trim();

    private static int? ParseInt(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool TryParseDouble(string token, out double value)
        => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// Raised when a CityGML buffer is malformed or carries no parseable building
/// geometry. Carries a stable <see cref="SceneGenerationErrorCodes"/> code so an
/// ingest executor can surface a problem-detail without leaking parser internals.
/// </summary>
public sealed class CityGmlFormatException : Exception
{
    /// <summary>Creates a <see cref="CityGmlFormatException"/>.</summary>
    /// <param name="code">Stable scene error code (see <see cref="SceneGenerationErrorCodes"/>).</param>
    /// <param name="message">Human-readable, non-sensitive description.</param>
    public CityGmlFormatException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Creates a <see cref="CityGmlFormatException"/> wrapping an inner cause.</summary>
    /// <param name="code">Stable scene error code (see <see cref="SceneGenerationErrorCodes"/>).</param>
    /// <param name="message">Human-readable, non-sensitive description.</param>
    /// <param name="innerException">The underlying parse failure.</param>
    public CityGmlFormatException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable error code forwarded through problem-detail helpers.</summary>
    public string Code { get; }
}
