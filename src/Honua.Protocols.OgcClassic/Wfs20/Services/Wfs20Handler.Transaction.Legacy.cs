// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// WFS 1.0.0 / 1.1.0 Transaction support, as an adapter over the 2.0 pipeline.
/// </summary>
/// <remarks>
/// QGIS is the reason this exists. Its WFS provider negotiates 2.0.0 for reads and then
/// issues every Transaction as WFS 1.0.0 - <c>wfs</c> in the unversioned
/// <c>http://www.opengis.net/wfs</c> namespace, <c>ogc:Filter</c>/<c>ogc:FeatureId</c>,
/// and GML 2 <c>gml:coordinates</c> - captured verbatim from QGIS 3.44.14 on the wire.
/// Honua parsed only the 2.0 shape, so every QGIS edit answered 501 and no stock QGIS
/// session could write through WFS-T at all, while the identical edit succeeded from
/// the OGC API Features provider.
/// <para>
/// The adapter rewrites a 1.x document into the 2.0 shape the existing parser
/// understands - namespace by namespace, plus the four element-level differences
/// (<c>FeatureId fid</c> to <c>ResourceId rid</c>, GML 2 <c>coordinates</c>/<c>coord</c> to
/// <c>posList</c>/<c>pos</c>, <c>outerBoundaryIs</c>/<c>innerBoundaryIs</c> to
/// <c>exterior</c>/<c>interior</c>) - and renders the response in the shape the
/// requesting version defined. Every edit still runs through the one 2.0 preparation
/// and commit path, so the semantics, the policies and the audit trail stay single.
/// </para>
/// <para>
/// Axis order is the one place a naive rewrite would corrupt data. GML 2 coordinates are
/// written x,y (longitude, latitude) whatever the srsName says, while GML 3.2 positions
/// follow the CRS axis order, which for <c>urn:ogc:def:crs:EPSG::4326</c> is latitude
/// first. The rewrite therefore swaps each tuple when the srsName resolves to a
/// north-east CRS, so that the 2.0 parser's own swap restores the client's longitude,
/// latitude.
/// </para>
/// </remarks>
internal sealed partial class Wfs20Handler
{
    // LegacyWfsNamespace is declared by the GET-side legacy partial (Wfs20Handler.Legacy.cs).
    internal const string LegacyOgcNamespace = "http://www.opengis.net/ogc";
    internal const string LegacyGmlNamespace = "http://www.opengis.net/gml";

    private static readonly XNamespace Wfs2 = Wfs20Utilities.WfsNamespace;
    private static readonly XNamespace Fes2 = Wfs20Utilities.FesNamespace;
    private static readonly XNamespace Gml32 = Wfs20Utilities.GmlNamespace;

    /// <summary>
    /// Rewrites a WFS 1.0.0 / 1.1.0 Transaction document into the 2.0 shape in place.
    /// Returns the legacy version when a rewrite happened, null for a 2.0 document.
    /// </summary>
    internal static string? TryNormaliseLegacyTransaction(XDocument document)
    {
        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        var version = root.Attribute("version")?.Value?.Trim();
        var isLegacyNamespace = string.Equals(root.Name.NamespaceName, LegacyWfsNamespace, StringComparison.Ordinal);
        var isLegacyVersion = version is not null && version.StartsWith("1.", StringComparison.Ordinal);
        if (!isLegacyNamespace && !isLegacyVersion)
        {
            return null;
        }

        var legacyVersion = isLegacyVersion ? version! : "1.0.0";
        foreach (var element in root.DescendantsAndSelf().ToArray())
        {
            NormaliseLegacyElement(element);
        }

        root.SetAttributeValue("version", Wfs20Utilities.Version);
        return legacyVersion;
    }

    private static void NormaliseLegacyElement(XElement element)
    {
        var ns = element.Name.NamespaceName;
        var local = element.Name.LocalName;

        if (string.Equals(ns, LegacyWfsNamespace, StringComparison.Ordinal))
        {
            element.Name = Wfs2 + local;
            return;
        }

        if (string.Equals(ns, LegacyOgcNamespace, StringComparison.Ordinal))
        {
            if (string.Equals(local, "FeatureId", StringComparison.OrdinalIgnoreCase))
            {
                var fid = element.Attribute("fid")?.Value;
                element.Name = Fes2 + "ResourceId";
                element.Attribute("fid")?.Remove();
                if (fid is not null)
                {
                    element.SetAttributeValue("rid", fid);
                }
                return;
            }

            element.Name = Fes2 + local;
            return;
        }

        if (string.Equals(ns, LegacyGmlNamespace, StringComparison.Ordinal))
        {
            NormaliseLegacyGmlElement(element, local);
        }
    }

    private static void NormaliseLegacyGmlElement(XElement element, string local)
    {
        switch (local)
        {
            case "outerBoundaryIs":
                element.Name = Gml32 + "exterior";
                return;
            case "innerBoundaryIs":
                element.Name = Gml32 + "interior";
                return;
            case "coordinates":
                {
                    // A GML 3.2 Point carries a single gml:pos; every other geometry carries a
                    // gml:posList. The 2.0 parser reads exactly that and rejects a Point whose
                    // coordinates arrive as a posList ("Point geometry must contain a gml:pos
                    // element"), which is what QGIS 3.44.14's point inserts hit before this
                    // distinction was made.
                    var swap = LegacyGeometryNeedsAxisSwap(element);
                    var tuples = ParseLegacyCoordinates(element, swap);
                    var isPoint = string.Equals(element.Parent?.Name.LocalName, "Point", StringComparison.Ordinal);
                    element.Name = Gml32 + (isPoint ? "pos" : "posList");
                    element.RemoveAttributes();
                    element.Value = string.Join(" ", tuples);
                    return;
                }
            case "coord":
                {
                    // GML 2 <gml:coord><gml:X/><gml:Y/></gml:coord>: one position per element.
                    var swap = LegacyGeometryNeedsAxisSwap(element);
                    var x = element.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "X", StringComparison.Ordinal))?.Value.Trim() ?? "";
                    var y = element.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Y", StringComparison.Ordinal))?.Value.Trim() ?? "";
                    element.Name = Gml32 + "pos";
                    element.RemoveAll();
                    element.Value = swap ? $"{y} {x}" : $"{x} {y}";
                    return;
                }
            default:
                element.Name = Gml32 + local;
                return;
        }
    }

    /// <summary>
    /// Whether the enclosing geometry's srsName resolves to a north-east (latitude first)
    /// CRS, in which case GML 2's x,y tuples must be swapped for the GML 3.2 parser.
    /// </summary>
    private static bool LegacyGeometryNeedsAxisSwap(XElement coordinateElement)
    {
        var srsName = coordinateElement.AncestorsAndSelf()
            .Select(ancestor => ancestor.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "srsName", StringComparison.OrdinalIgnoreCase))?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return false;
        }

        return SpatialReferenceHelpers.TryParseCrsDefinition(srsName, out var crs)
            && crs.AxisOrder == AxisOrder.NorthEast;
    }

    private static IEnumerable<string> ParseLegacyCoordinates(XElement coordinatesElement, bool swap)
    {
        // GML 2.1.2: tuples separated by ts (default space), ordinates by cs (default comma).
        var cs = coordinatesElement.Attribute("cs")?.Value is { Length: > 0 } csValue ? csValue : ",";
        var ts = coordinatesElement.Attribute("ts")?.Value is { Length: > 0 } tsValue ? tsValue : " ";
        var raw = coordinatesElement.Value.Trim();
        var tuples = ts == " "
            ? raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : raw.Split(ts, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var tuple in tuples)
        {
            var ordinates = tuple.Split(cs, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ordinates.Length < 2)
            {
                throw new ArgumentException($"GML coordinates tuple '{tuple}' does not carry two ordinates.");
            }

            yield return swap ? $"{ordinates[1]} {ordinates[0]}" : $"{ordinates[0]} {ordinates[1]}";
        }
    }

    /// <summary>
    /// The TransactionResponse in the shape the requesting 1.x version defined. QGIS reads
    /// <c>WFS_TransactionResponse/TransactionResult/Status/SUCCESS</c> and
    /// <c>InsertResult/ogc:FeatureId</c> for 1.0.0, and <c>TransactionResponse/TransactionSummary</c>
    /// with <c>InsertResults/Feature/ogc:FeatureId</c> for 1.1.0.
    /// </summary>
    private static string BuildLegacyTransactionResponseXml(
        string legacyVersion,
        IReadOnlyList<(PreparedTransactionOperation Operation, EditOperationResult Result)> inserted,
        IReadOnlyList<(PreparedTransactionOperation Operation, EditOperationResult Result)> receipts,
        int updatedCount,
        int deletedCount)
    {
        var allSucceeded = receipts.All(static receipt => receipt.Result.IsSuccess);
        var isOneZero = legacyVersion.StartsWith("1.0", StringComparison.Ordinal);

        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            if (isOneZero)
            {
                writer.WriteStartElement("wfs", "WFS_TransactionResponse", LegacyWfsNamespace);
                writer.WriteAttributeString("xmlns", "ogc", null, LegacyOgcNamespace);
                writer.WriteAttributeString("version", "1.0.0");
                foreach (var (operation, result) in inserted)
                {
                    writer.WriteStartElement("wfs", "InsertResult", LegacyWfsNamespace);
                    if (!string.IsNullOrWhiteSpace(operation.Handle))
                    {
                        writer.WriteAttributeString("handle", operation.Handle);
                    }
                    WriteLegacyFeatureId(writer, operation, result.ObjectId ?? 0);
                    writer.WriteEndElement();
                }
                writer.WriteStartElement("wfs", "TransactionResult", LegacyWfsNamespace);
                writer.WriteStartElement("wfs", "Status", LegacyWfsNamespace);
                writer.WriteStartElement("wfs", allSucceeded ? "SUCCESS" : "FAILED", LegacyWfsNamespace);
                writer.WriteEndElement();
                writer.WriteEndElement();
                if (!allSucceeded)
                {
                    var message = receipts.FirstOrDefault(static receipt => !receipt.Result.IsSuccess).Result.ErrorMessage;
                    writer.WriteElementString("wfs", "Message", LegacyWfsNamespace, message ?? "Operation failed.");
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            else
            {
                writer.WriteStartElement("wfs", "TransactionResponse", LegacyWfsNamespace);
                writer.WriteAttributeString("xmlns", "ogc", null, LegacyOgcNamespace);
                writer.WriteAttributeString("version", "1.1.0");
                writer.WriteStartElement("wfs", "TransactionSummary", LegacyWfsNamespace);
                writer.WriteElementString("wfs", "totalInserted", LegacyWfsNamespace, inserted.Count.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("wfs", "totalUpdated", LegacyWfsNamespace, updatedCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("wfs", "totalDeleted", LegacyWfsNamespace, deletedCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
                if (inserted.Count > 0)
                {
                    writer.WriteStartElement("wfs", "InsertResults", LegacyWfsNamespace);
                    foreach (var (operation, result) in inserted)
                    {
                        writer.WriteStartElement("wfs", "Feature", LegacyWfsNamespace);
                        if (!string.IsNullOrWhiteSpace(operation.Handle))
                        {
                            writer.WriteAttributeString("handle", operation.Handle);
                        }
                        WriteLegacyFeatureId(writer, operation, result.ObjectId ?? 0);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndDocument();
        });
    }

    private static void WriteLegacyFeatureId(XmlWriter writer, PreparedTransactionOperation operation, long objectId)
    {
        writer.WriteStartElement("ogc", "FeatureId", LegacyOgcNamespace);
        writer.WriteAttributeString("fid", BuildFeatureId(operation.Descriptor, objectId));
        writer.WriteEndElement();
    }
}
