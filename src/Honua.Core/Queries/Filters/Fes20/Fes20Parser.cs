// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters.Fes20;

/// <summary>
/// Parser for OGC Filter Encoding 2.0 XML filters that produces the shared Filter AST
/// </summary>
public static class Fes20Parser
{
    private const string FesNamespace = "http://www.opengis.net/fes/2.0";
    private const string GmlNamespace = "http://www.opengis.net/gml/3.2";

    /// <summary>
    /// Parses a FES 2.0 filter XML element into the shared Filter AST
    /// </summary>
    public static FilterExpression ParseFilter(XElement filterElement)
    {
        if (filterElement.Name.NamespaceName != FesNamespace || filterElement.Name.LocalName != "Filter")
        {
            throw new Fes20ParseException($"Expected Filter element in namespace {FesNamespace}, got {filterElement.Name}");
        }

        var firstChild = filterElement.Elements().FirstOrDefault();
        if (firstChild == null)
        {
            throw new Fes20ParseException("Filter element must contain at least one child element");
        }

        return ParseExpression(firstChild);
    }

    /// <summary>
    /// Parses a FES 2.0 filter XML string into the shared Filter AST
    /// </summary>
    public static FilterExpression ParseFilter(string filterXml)
    {
        try
        {
            using var stringReader = new StringReader(filterXml);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var doc = XDocument.Load(xmlReader, LoadOptions.None);
            var filterElement = doc.Root ?? throw new Fes20ParseException("Invalid XML: no root element");
            return ParseFilter(filterElement);
        }
        catch (XmlException ex)
        {
            throw new Fes20ParseException($"Invalid filter XML: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses any FES 2.0 expression element
    /// </summary>
    private static FilterExpression ParseExpression(XElement element)
    {
        if (element.Name.NamespaceName != FesNamespace)
        {
            throw new Fes20ParseException($"Unexpected namespace: {element.Name.NamespaceName}. Expected {FesNamespace}");
        }

        return element.Name.LocalName switch
        {
            // Logical operators
            "And" => ParseAnd(element),
            "Or" => ParseOr(element),
            "Not" => ParseNot(element),

            // Comparison operators
            "PropertyIsEqualTo" => ParseComparison(element, BinaryOperator.Equal),
            "PropertyIsNotEqualTo" => ParseComparison(element, BinaryOperator.NotEqual),
            "PropertyIsLessThan" => ParseComparison(element, BinaryOperator.LessThan),
            "PropertyIsGreaterThan" => ParseComparison(element, BinaryOperator.GreaterThan),
            "PropertyIsLessThanOrEqualTo" => ParseComparison(element, BinaryOperator.LessThanOrEqual),
            "PropertyIsGreaterThanOrEqualTo" => ParseComparison(element, BinaryOperator.GreaterThanOrEqual),
            "PropertyIsLike" => ParseLike(element),
            "PropertyIsNull" => ParseIsNull(element),
            "PropertyIsBetween" => ParseBetween(element),

            // Spatial operators
            "BBOX" => ParseBBox(element),
            "Intersects" => ParseSpatialBinary(element, SpatialOperator.Intersects),
            "Contains" => ParseSpatialBinary(element, SpatialOperator.Contains),
            "Within" => ParseSpatialBinary(element, SpatialOperator.Within),
            "Crosses" => ParseSpatialBinary(element, SpatialOperator.Crosses),
            "Touches" => ParseSpatialBinary(element, SpatialOperator.Touches),
            "Overlaps" => ParseSpatialBinary(element, SpatialOperator.Overlaps),
            "Disjoint" => ParseSpatialBinary(element, SpatialOperator.Disjoint),
            "Equals" => ParseSpatialBinary(element, SpatialOperator.Equals),
            "DWithin" => ParseDWithin(element),
            "Beyond" => ParseBeyond(element),

            // Temporal operators (basic support)
            "During" => ParseTemporal(element),
            "Before" => ParseTemporal(element),
            "After" => ParseTemporal(element),

            // Resource identifier
            "ResourceId" => ParseResourceId(element),

            // Property and literal elements
            "ValueReference" => new PropertyReference(element.Value.Trim()),
            "Literal" => ParseLiteral(element),

            _ => throw new Fes20ParseException($"Unsupported FES 2.0 element: {element.Name.LocalName}")
        };
    }

    /// <summary>
    /// Parses logical AND operation
    /// </summary>
    private static FilterExpression ParseAnd(XElement element)
    {
        var children = element.Elements().ToArray();
        if (children.Length < 2)
        {
            throw new Fes20ParseException("And element must contain at least 2 child elements");
        }

        var result = ParseExpression(children[0]);
        for (int i = 1; i < children.Length; i++)
        {
            result = new BinaryExpression(result, BinaryOperator.And, ParseExpression(children[i]));
        }

        return result;
    }

    /// <summary>
    /// Parses logical OR operation
    /// </summary>
    private static FilterExpression ParseOr(XElement element)
    {
        var children = element.Elements().ToArray();
        if (children.Length < 2)
        {
            throw new Fes20ParseException("Or element must contain at least 2 child elements");
        }

        var result = ParseExpression(children[0]);
        for (int i = 1; i < children.Length; i++)
        {
            result = new BinaryExpression(result, BinaryOperator.Or, ParseExpression(children[i]));
        }

        return result;
    }

    /// <summary>
    /// Parses logical NOT operation
    /// </summary>
    private static UnaryExpression ParseNot(XElement element)
    {
        var child = element.Elements().FirstOrDefault();
        if (child == null)
        {
            throw new Fes20ParseException("Not element must contain exactly 1 child element");
        }

        return new UnaryExpression(UnaryOperator.Not, ParseExpression(child));
    }

    /// <summary>
    /// Parses comparison operations
    /// </summary>
    private static BinaryExpression ParseComparison(XElement element, BinaryOperator op)
    {
        var children = element.Elements().ToArray();
        if (children.Length != 2)
        {
            throw new Fes20ParseException($"{element.Name.LocalName} element must contain exactly 2 child elements");
        }

        var left = ParseExpression(children[0]);
        var right = ParseExpression(children[1]);

        return new BinaryExpression(left, op, right);
    }

    /// <summary>
    /// Parses PropertyIsLike operation
    /// </summary>
    private static BinaryExpression ParseLike(XElement element)
    {
        var wildCard = element.Attribute("wildCard")?.Value ?? "%";
        var singleChar = element.Attribute("singleChar")?.Value ?? "_";
        var escapeChar = element.Attribute("escapeChar")?.Value ?? "\\";
        var matchCase = !string.Equals(element.Attribute("matchCase")?.Value, "false", StringComparison.OrdinalIgnoreCase);

        var children = element.Elements().ToArray();
        if (children.Length != 2)
        {
            throw new Fes20ParseException("PropertyIsLike element must contain exactly 2 child elements");
        }

        var propertyRef = ParseExpression(children[0]);
        var pattern = ParseExpression(children[1]);

        // Convert FES wildcards to SQL LIKE pattern
        if (pattern is Literal literal && literal.Value is string patternStr)
        {
            var sqlPattern = ConvertFesPatternToSql(patternStr, wildCard, singleChar, escapeChar);
            if (!matchCase)
            {
                sqlPattern = sqlPattern.ToLowerInvariant();
            }

            pattern = new Literal(sqlPattern, LiteralType.Text);
        }
        else if (!matchCase)
        {
            pattern = new FunctionCall("LOWER", [pattern]);
        }

        if (!matchCase)
        {
            propertyRef = new FunctionCall("LOWER", [propertyRef]);
        }

        return new BinaryExpression(propertyRef, BinaryOperator.Like, pattern);
    }

    /// <summary>
    /// Parses PropertyIsNull operation
    /// </summary>
    private static UnaryExpression ParseIsNull(XElement element)
    {
        var child = element.Elements().FirstOrDefault();
        if (child == null)
        {
            throw new Fes20ParseException("PropertyIsNull element must contain exactly 1 child element");
        }

        var property = ParseExpression(child);
        return new UnaryExpression(UnaryOperator.IsNull, property);
    }

    /// <summary>
    /// Parses PropertyIsBetween operation
    /// </summary>
    private static BinaryExpression ParseBetween(XElement element)
    {
        var valueRef = element.Elements().FirstOrDefault(e => e.Name.LocalName == "ValueReference");
        var lowerBoundary = element.Elements().FirstOrDefault(e => e.Name.LocalName == "LowerBoundary");
        var upperBoundary = element.Elements().FirstOrDefault(e => e.Name.LocalName == "UpperBoundary");

        if (valueRef == null || lowerBoundary == null || upperBoundary == null)
        {
            throw new Fes20ParseException("PropertyIsBetween element must contain ValueReference, LowerBoundary, and UpperBoundary elements");
        }

        var property = ParseExpression(valueRef);
        var lowerChild = lowerBoundary.Elements().FirstOrDefault()
            ?? throw new Fes20ParseException("LowerBoundary element must contain a child element");
        var upperChild = upperBoundary.Elements().FirstOrDefault()
            ?? throw new Fes20ParseException("UpperBoundary element must contain a child element");
        var lower = ParseExpression(lowerChild);
        var upper = ParseExpression(upperChild);

        // Convert to: property >= lower AND property <= upper
        var greaterEqual = new BinaryExpression(property, BinaryOperator.GreaterThanOrEqual, lower);
        var lessEqual = new BinaryExpression(property, BinaryOperator.LessThanOrEqual, upper);

        return new BinaryExpression(greaterEqual, BinaryOperator.And, lessEqual);
    }

    /// <summary>
    /// Parses BBOX spatial operation
    /// </summary>
    private static SpatialPredicate ParseBBox(XElement element)
    {
        var children = element.Elements().ToArray();
        if (children.Length != 2)
        {
            throw new Fes20ParseException("BBOX element must contain exactly 2 child elements");
        }

        var propertyRef = children[0];
        var envelope = children[1];

        if (propertyRef.Name.LocalName != "ValueReference")
        {
            throw new Fes20ParseException("First child of BBOX must be ValueReference");
        }

        var property = new PropertyReference(propertyRef.Value.Trim());
        var geometry = ParseGeometry(envelope);

        return new SpatialPredicate(SpatialOperator.Intersects, property, geometry);
    }

    /// <summary>
    /// Parses binary spatial operations
    /// </summary>
    private static SpatialPredicate ParseSpatialBinary(XElement element, SpatialOperator op)
    {
        var children = element.Elements().ToArray();
        if (children.Length != 2)
        {
            throw new Fes20ParseException($"{element.Name.LocalName} element must contain exactly 2 child elements");
        }

        var propertyRef = children[0];
        var geometryElement = children[1];

        if (propertyRef.Name.LocalName != "ValueReference")
        {
            throw new Fes20ParseException($"First child of {element.Name.LocalName} must be ValueReference");
        }

        var property = new PropertyReference(propertyRef.Value.Trim());
        var geometry = ParseGeometry(geometryElement);

        return new SpatialPredicate(op, property, geometry);
    }

    /// <summary>
    /// Parses DWithin spatial operation
    /// </summary>
    private static SpatialDistancePredicate ParseDWithin(XElement element)
    {
        var children = element.Elements().ToArray();
        if (children.Length != 3)
        {
            throw new Fes20ParseException("DWithin element must contain exactly 3 child elements");
        }

        var propertyRef = children[0];
        var geometryElement = children[1];
        var distance = children[2];

        if (propertyRef.Name.LocalName != "ValueReference")
        {
            throw new Fes20ParseException("First child of DWithin must be ValueReference");
        }

        if (distance.Name.LocalName != "Distance")
        {
            throw new Fes20ParseException("Third child of DWithin must be Distance");
        }

        var property = new PropertyReference(propertyRef.Value.Trim());
        var geometry = ParseGeometry(geometryElement);

        return new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            property,
            geometry,
            ParseDistanceLiteral(distance));
    }

    /// <summary>
    /// Parses Beyond spatial operation
    /// </summary>
    private static SpatialDistancePredicate ParseBeyond(XElement element)
    {
        var children = element.Elements().ToArray();
        if (children.Length != 3)
        {
            throw new Fes20ParseException("Beyond element must contain exactly 3 child elements");
        }

        var propertyRef = children[0];
        var geometryElement = children[1];
        var distance = children[2];

        if (propertyRef.Name.LocalName != "ValueReference")
        {
            throw new Fes20ParseException("First child of Beyond must be ValueReference");
        }

        if (distance.Name.LocalName != "Distance")
        {
            throw new Fes20ParseException("Third child of Beyond must be Distance");
        }

        var property = new PropertyReference(propertyRef.Value.Trim());
        var geometry = ParseGeometry(geometryElement);

        return new SpatialDistancePredicate(
            SpatialOperator.Beyond,
            property,
            geometry,
            ParseDistanceLiteral(distance));
    }

    /// <summary>
    /// Parses temporal operations (basic support)
    /// </summary>
    private static FilterExpression ParseTemporal(XElement element)
    {
        // TODO: Implement temporal filter parsing
        // For now, throw exception as temporal filters are not yet supported
        throw new Fes20ParseException($"Temporal operator {element.Name.LocalName} is not yet supported");
    }

    /// <summary>
    /// Parses ResourceId element
    /// </summary>
    private static BinaryExpression ParseResourceId(XElement element)
    {
        var rid = element.Attribute("rid")?.Value;
        if (string.IsNullOrEmpty(rid))
        {
            throw new Fes20ParseException("ResourceId element must have a 'rid' attribute");
        }

        // Convert to property equality: id = 'rid'
        return new BinaryExpression(
            new PropertyReference("id"),
            BinaryOperator.Equal,
            new Literal(rid, LiteralType.Text));
    }

    /// <summary>
    /// Parses literal values
    /// </summary>
    private static Literal ParseLiteral(XElement element)
    {
        var value = element.Value;
        var type = element.Attribute("type")?.Value;

        return InferLiteralType(value, type);
    }

    /// <summary>
    /// Parses geometry elements
    /// </summary>
    private static GeometryLiteral ParseGeometry(XElement element)
    {
        if (element.Name.LocalName == "Envelope" &&
            (element.Name.NamespaceName == FesNamespace || element.Name.NamespaceName == GmlNamespace))
        {
            return ParseEnvelope(element);
        }

        if (element.Name.NamespaceName == GmlNamespace)
        {
            return ParseGmlGeometry(element);
        }

        throw new Fes20ParseException($"Unsupported geometry element: {element.Name}");
    }

    /// <summary>
    /// Parses FES Envelope element
    /// </summary>
    private static GeometryLiteral ParseEnvelope(XElement element)
    {
        var srsName = element.Attribute("srsName")?.Value ?? "EPSG:4326";
        var lowerCorner = element.Elements().FirstOrDefault(e => e.Name.LocalName == "lowerCorner")?.Value;
        var upperCorner = element.Elements().FirstOrDefault(e => e.Name.LocalName == "upperCorner")?.Value;

        if (string.IsNullOrEmpty(lowerCorner) || string.IsNullOrEmpty(upperCorner))
        {
            throw new Fes20ParseException("Envelope must contain lowerCorner and upperCorner elements");
        }

        var lowerParts = lowerCorner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var upperParts = upperCorner.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (lowerParts.Length < 2 || upperParts.Length < 2)
        {
            throw new Fes20ParseException("Envelope coordinates must have at least 2 dimensions");
        }

        if (!double.TryParse(lowerParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) ||
            !double.TryParse(lowerParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY) ||
            !double.TryParse(upperParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX) ||
            !double.TryParse(upperParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
        {
            throw new Fes20ParseException("Invalid envelope coordinates");
        }

        // Convert to WKB polygon
        // TODO: Implement proper envelope to WKB conversion
        // For now, create a simple polygon representation
        var wkbGeometry = CreateEnvelopeWkb(minX, minY, maxX, maxY);

        return new GeometryLiteral(
            wkbGeometry,
            ParseSrid(srsName),
            "FES:Envelope");
    }

    /// <summary>
    /// Parses GML geometry elements
    /// </summary>
    private static GeometryLiteral ParseGmlGeometry(XElement element)
    {
        // TODO: Implement GML geometry parsing
        // For now, throw exception as full GML parsing is complex
        throw new Fes20ParseException($"GML geometry parsing is not yet implemented for {element.Name.LocalName}");
    }

    /// <summary>
    /// Infers literal type from value and optional type attribute
    /// </summary>
    private static Literal InferLiteralType(string value, string? typeHint)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new Literal(null, LiteralType.Null);
        }

        if (typeHint != null)
        {
            try
            {
                return typeHint.ToLowerInvariant() switch
                {
                    "string" or "xs:string" => new Literal(value, LiteralType.Text),
                    "int" or "integer" or "xs:int" or "xs:integer" => new Literal(int.Parse(value, CultureInfo.InvariantCulture), LiteralType.Number),
                    "double" or "xs:double" or "decimal" or "xs:decimal" => new Literal(double.Parse(value, CultureInfo.InvariantCulture), LiteralType.Number),
                    "boolean" or "xs:boolean" => new Literal(bool.Parse(value), LiteralType.Boolean),
                    "date" or "xs:date" or "datetime" or "xs:datetime" => new Literal(
                        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        LiteralType.DateTime),
                    _ => new Literal(value, LiteralType.Text)
                };
            }
            catch (FormatException ex)
            {
                throw new Fes20ParseException($"Cannot parse literal value '{value}' as {typeHint}: {ex.Message}", ex);
            }
            catch (OverflowException ex)
            {
                throw new Fes20ParseException($"Literal value '{value}' overflows type {typeHint}: {ex.Message}", ex);
            }
        }

        // Try to infer type from value
        if (bool.TryParse(value, out var boolValue))
            return new Literal(boolValue, LiteralType.Boolean);

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            return new Literal(intValue, LiteralType.Number);

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
            return new Literal(doubleValue, LiteralType.Number);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateValue))
            return new Literal(dateValue, LiteralType.DateTime);

        return new Literal(value, LiteralType.Text);
    }

    /// <summary>
    /// Converts FES wildcard pattern to SQL LIKE pattern
    /// </summary>
    private static string ConvertFesPatternToSql(string pattern, string wildCard, string singleChar, string escapeChar)
    {
        if (string.IsNullOrEmpty(wildCard) || string.IsNullOrEmpty(singleChar) || string.IsNullOrEmpty(escapeChar))
        {
            throw new Fes20ParseException("PropertyIsLike wildcard, singleChar, and escapeChar values must be non-empty.");
        }

        var builder = new StringBuilder(pattern.Length * 2);

        for (var index = 0; index < pattern.Length;)
        {
            if (MatchesToken(pattern, escapeChar, index))
            {
                index += escapeChar.Length;
                if (index >= pattern.Length)
                {
                    AppendEscapedSqlLikeText(builder, escapeChar);
                    break;
                }

                if (MatchesToken(pattern, wildCard, index))
                {
                    AppendEscapedSqlLikeText(builder, wildCard);
                    index += wildCard.Length;
                    continue;
                }

                if (MatchesToken(pattern, singleChar, index))
                {
                    AppendEscapedSqlLikeText(builder, singleChar);
                    index += singleChar.Length;
                    continue;
                }

                if (MatchesToken(pattern, escapeChar, index))
                {
                    AppendEscapedSqlLikeText(builder, escapeChar);
                    index += escapeChar.Length;
                    continue;
                }

                AppendEscapedSqlLikeCharacter(builder, pattern[index]);
                index++;
                continue;
            }

            if (MatchesToken(pattern, wildCard, index))
            {
                builder.Append('%');
                index += wildCard.Length;
                continue;
            }

            if (MatchesToken(pattern, singleChar, index))
            {
                builder.Append('_');
                index += singleChar.Length;
                continue;
            }

            AppendEscapedSqlLikeCharacter(builder, pattern[index]);
            index++;
        }

        return builder.ToString();
    }

    private static Literal ParseDistanceLiteral(XElement distanceElement)
    {
        if (!double.TryParse(distanceElement.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance))
        {
            throw new Fes20ParseException("Distance element must contain a valid numeric value.");
        }

        return new Literal(distance, LiteralType.Number);
    }

    private static bool MatchesToken(string text, string token, int index)
        => index + token.Length <= text.Length &&
           string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static void AppendEscapedSqlLikeText(StringBuilder builder, string value)
    {
        foreach (var character in value)
        {
            AppendEscapedSqlLikeCharacter(builder, character);
        }
    }

    private static void AppendEscapedSqlLikeCharacter(StringBuilder builder, char character)
    {
        if (character is '%' or '_' or '\\')
        {
            builder.Append('\\');
        }

        builder.Append(character);
    }

    /// <summary>
    /// Parses SRID from CRS identifier
    /// </summary>
    private static int ParseSrid(string srsName)
    {
        // Handle various CRS identifier formats
        if (srsName.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(srsName[5..], out var epsgCode))
                return epsgCode;
        }

        if (srsName.StartsWith("urn:ogc:def:crs:EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = srsName.Split(':');
            if (parts.Length >= 7 && int.TryParse(parts[6], out var urnEpsgCode))
                return urnEpsgCode;
        }

        // Default to WGS84
        return 4326;
    }

    /// <summary>
    /// Creates WKB representation for an envelope/bbox
    /// </summary>
    private static byte[] CreateEnvelopeWkb(double minX, double minY, double maxX, double maxY)
    {
        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var polygon = geometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        ]);

        return new WKBWriter(ByteOrder.LittleEndian, handleSRID: false).Write(polygon);
    }
}

/// <summary>
/// Exception thrown when FES 2.0 parsing fails
/// </summary>
public sealed class Fes20ParseException : Exception
{
    /// <summary>
    /// Initializes a new exception with the supplied parse error message.
    /// </summary>
    /// <param name="message">The parse failure message.</param>
    public Fes20ParseException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new exception with the supplied parse error message and inner exception.
    /// </summary>
    /// <param name="message">The parse failure message.</param>
    /// <param name="innerException">The underlying parse failure.</param>
    public Fes20ParseException(string message, Exception innerException) : base(message, innerException) { }
}
