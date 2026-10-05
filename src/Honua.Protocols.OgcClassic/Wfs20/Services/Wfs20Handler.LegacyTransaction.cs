// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed partial class Wfs20Handler
{
    private static readonly string[] LegacyTransactionSummaryNames = ["totalInserted", "totalUpdated", "totalDeleted"];

    // QGIS uses WFS 1.0/1.1 Transaction even when the read connection negotiated 2.0.
    // Adapt only the wire representation; authorization and edits remain canonical.
    internal static XElement NormalizeLegacyTransaction(XElement root)
    {
        var version = root.Attribute("version")?.Value;
        if (version is not ("1.0.0" or "1.1.0"))
        {
            throw new ArgumentException("Expected a WFS 1.0 or 1.1 transaction.");
        }

        var normalized = new XElement(root);
        foreach (var action in normalized.Elements())
        {
            if (action.Name.LocalName is not ("Insert" or "Update" or "Delete"))
            {
                throw new NotSupportedException("Legacy WFS transactions support Insert, Update and Delete.");
            }
        }

        foreach (var filter in normalized.Descendants(XName.Get("Filter", OgcFilterNamespace)).ToArray())
        {
            foreach (var geometry in filter.Descendants().Where(element =>
                         element.Name.NamespaceName == GmlLegacyNamespace &&
                         element.Parent?.Name.NamespaceName != GmlLegacyNamespace))
            {
                EnsureTwoDimensionalLegacyGeometry(geometry);
            }

            var convertedFilter = NormalizeLegacyFilterElement(filter);
            if (version == "1.0.0")
            {
                convertedFilter.AddAnnotation(new LegacyWfs10Coordinates());
            }

            filter.ReplaceWith(convertedFilter);
        }

        // Preserve application properties, including names which happen to match GML
        // or filter element names. Only actual GML subtrees are normalized.
        foreach (var geometry in normalized.Descendants()
                     .Where(element => element.Name.NamespaceName == GmlLegacyNamespace &&
                         element.Parent?.Name.NamespaceName != GmlLegacyNamespace).ToArray())
        {
            EnsureTwoDimensionalLegacyGeometry(geometry);
            var converted = NormalizeLegacyFilterElement(geometry);
            if (version == "1.0.0")
            {
                // Internal provenance, not a client-settable XML attribute. WFS 1.0
                // uses x/y even with a geographic urn label or an omitted srsName.
                converted.AddAnnotation(new LegacyWfs10Coordinates());
            }

            geometry.ReplaceWith(converted);
        }

        return normalized;
    }

    private static void EnsureTwoDimensionalLegacyGeometry(XElement geometry)
    {
        foreach (var element in geometry.DescendantsAndSelf())
        {
            if (element.Attribute("srsDimension") is { Value: not "2" } ||
                element.Name.LocalName == "Z")
            {
                throw new NotSupportedException("Legacy WFS transaction geometries currently require two dimensions.");
            }

            if (element.Name.LocalName == "coordinates")
            {
                var tuples = SplitLegacyCoordinateTuples(element.Value, element.Attribute("ts")?.Value ?? " ");
                var separator = element.Attribute("cs")?.Value ?? ",";
                if (tuples.Any(tuple => tuple.Split(separator, StringSplitOptions.TrimEntries).Length != 2))
                {
                    throw new NotSupportedException("Legacy WFS transaction geometries currently require two dimensions.");
                }
            }
            else if (element.Name.LocalName == "pos" &&
                     element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length != 2)
            {
                throw new NotSupportedException("Legacy WFS transaction geometries currently require two dimensions.");
            }
        }
    }

    private sealed class LegacyWfs10Coordinates
    {
    }

    internal static XElement PrepareLegacyTransactionFilter(XElement filter, int defaultSrid)
    {
        if (filter.Annotation<LegacyWfs10Coordinates>() is null)
        {
            return filter;
        }

        // FES uses the CRS axis order. Rewrite legacy XY ordinates only after the
        // target layer's default CRS is known, including filters without srsName.
        var canonical = new XElement(filter);
        foreach (var geometry in canonical.Descendants().Where(element =>
                     element.Name.NamespaceName == Wfs20Utilities.GmlNamespace &&
                     element.Parent?.Name.NamespaceName != Wfs20Utilities.GmlNamespace))
        {
            var axisOrder = SpatialReference.Create(defaultSrid).IsGeographic
                ? AxisOrder.NorthEast
                : AxisOrder.EastNorth;
            var srsName = geometry.Attribute("srsName")?.Value;
            if (!string.IsNullOrWhiteSpace(srsName))
            {
                if (!SpatialReferenceHelpers.TryParseCrsDefinition(srsName, out var crs))
                {
                    throw new ArgumentException($"Unsupported or unrecognized srsName '{srsName}'.");
                }

                axisOrder = crs.AxisOrder;
            }

            if (axisOrder != AxisOrder.NorthEast)
            {
                continue;
            }

            foreach (var coordinates in geometry.Descendants().Where(element =>
                         element.Name.NamespaceName == Wfs20Utilities.GmlNamespace &&
                         element.Name.LocalName is "pos" or "posList" or "lowerCorner" or "upperCorner"))
            {
                Fes20Parser.EnsureGeometryTextWithinLimits(coordinates.Value);
                var ordinates = coordinates.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (ordinates.Length % 2 != 0)
                {
                    throw Fes20ParseException.Reportable("Legacy filter coordinates require two dimensions.");
                }

                Fes20Parser.EnsureCoordinateCountWithinLimits(ordinates.Length / 2);
                for (var index = 0; index < ordinates.Length; index += 2)
                {
                    (ordinates[index], ordinates[index + 1]) = (ordinates[index + 1], ordinates[index]);
                }

                coordinates.Value = string.Join(' ', ordinates);
            }
        }

        return canonical;
    }

    internal static string FormatLegacyTransactionResponse(string canonicalResponse, string version)
    {
        XNamespace wfs = LegacyWfsNamespace;
        XNamespace ogc = OgcFilterNamespace;
        XNamespace wfs20 = Wfs20Utilities.WfsNamespace;
        XNamespace fes = Wfs20Utilities.FesNamespace;
        XNamespace honua = FeatureNamespaceUri;
        var canonical = XElement.Parse(canonicalResponse);
        if (version == "1.1.0")
        {
            var receipts = canonical.Element(honua + "OperationResults");
            var failures = receipts?.Elements(honua + "OperationResult")
                .Where(receipt => receipt.Attribute("committed")?.Value != "true")
                .Select(receipt => new XElement(wfs20 + "Action",
                    new XAttribute("locator", receipt.Attribute("handle")?.Value
                        ?? $"operation-{receipt.Attribute("sequence")?.Value ?? "unknown"}"),
                    new XAttribute("code", receipt.Attribute("committed")?.Value == "unknown"
                        ? "CommitOutcomeUnknown" : "OperationFailed"),
                    new XElement(wfs20 + "Message", receipt.Element(honua + "Error")?.Value
                        ?? "Operation did not report a committed result.")))
                .ToArray() ?? [];
            receipts?.Remove();
            if (failures.Length > 0)
            {
                var results = new XElement(wfs20 + "TransactionResults", failures);
                if (canonical.Element(wfs20 + "InsertResults") is { } inserts)
                {
                    inserts.AddBeforeSelf(results);
                }
                else
                {
                    canonical.Add(results);
                }
            }

            if (canonical.Element(wfs20 + "TransactionSummary") is { } summary)
            {
                var totals = LegacyTransactionSummaryNames
                    .Select(name => new XElement(wfs20 + name, summary.Element(wfs20 + name)?.Value ?? "0"))
                    .ToArray();
                summary.ReplaceNodes(totals);
            }

            // WFS 1.1 keeps the summary/feature structure, but uses the legacy
            // WFS namespace and ogc:FeatureId instead of fes:ResourceId.
            foreach (var element in canonical.DescendantsAndSelf())
            {
                if (element.Name.Namespace == wfs20)
                {
                    element.Name = wfs + element.Name.LocalName;
                }
                else if (element.Name == fes + "ResourceId")
                {
                    var id = element.Attribute("rid")!.Value;
                    element.Name = ogc + "FeatureId";
                    element.Attribute("rid")!.Remove();
                    element.SetAttributeValue("fid", id);
                }
            }

            canonical.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Remove();
            canonical.SetAttributeValue("version", version);
            canonical.Attribute("timeStamp")?.Remove();
            return canonical.ToString(SaveOptions.DisableFormatting);
        }

        if (version != "1.0.0")
        {
            throw new ArgumentException("Expected a WFS 1.0 or 1.1 transaction response.");
        }

        var response = new XElement(wfs + "WFS_TransactionResponse",
            new XAttribute("version", "1.0.0"),
            new XAttribute(XNamespace.Xmlns + "wfs", wfs),
            new XAttribute(XNamespace.Xmlns + "ogc", ogc));
        foreach (var feature in canonical.Element(wfs20 + "InsertResults")?.Elements(wfs20 + "Feature") ?? [])
        {
            var insert = new XElement(wfs + "InsertResult");
            if (feature.Attribute("handle") is { } handle)
            {
                insert.Add(new XAttribute("handle", handle.Value));
            }

            foreach (var identity in feature.Elements(fes + "ResourceId"))
            {
                insert.Add(new XElement(ogc + "FeatureId", new XAttribute("fid", identity.Attribute("rid")!.Value)));
            }

            response.Add(insert);
        }

        // A best-effort/unknown commit must never become SUCCESS during adaptation.
        var hasFailure = canonical.Descendants(honua + "OperationResult")
            .Any(receipt => receipt.Attribute("committed")?.Value != "true");
        response.Add(new XElement(wfs + "TransactionResult",
            new XElement(wfs + "Status", new XElement(wfs + (hasFailure ? "PARTIAL" : "SUCCESS")))));
        return response.ToString(SaveOptions.DisableFormatting);
    }
}
