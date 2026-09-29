// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed partial class Wfs20Handler
{
    // QGIS uses WFS 1.0 Transaction even when the read connection negotiated 2.0.
    // Adapt only the wire representation; authorization and edits remain canonical.
    internal static XElement NormalizeWfs10Transaction(XElement root)
    {
        var normalized = new XElement(root);
        foreach (var action in normalized.Elements())
        {
            if (action.Name.LocalName is not ("Insert" or "Update" or "Delete"))
            {
                throw new NotSupportedException("WFS 1.0 transactions support Insert, Update and Delete.");
            }
        }

        foreach (var filter in normalized.Descendants(XName.Get("Filter", OgcFilterNamespace)).ToArray())
        {
            filter.ReplaceWith(NormalizeLegacyFilterElement(filter));
        }

        // Preserve application properties, including names which happen to match GML
        // or filter element names. Only actual GML subtrees are normalized.
        foreach (var geometry in normalized.Descendants()
                     .Where(element => element.Name.NamespaceName == GmlLegacyNamespace &&
                         element.Parent?.Name.NamespaceName != GmlLegacyNamespace).ToArray())
        {
            EnsureTwoDimensionalLegacyGeometry(geometry);
            var converted = NormalizeLegacyFilterElement(geometry);
            foreach (var element in converted.DescendantsAndSelf())
            {
                if (element.Attribute("srsName") is { } srs)
                {
                    if (!SpatialReferenceHelpers.TryParseCrsDefinition(srs.Value, out var crs))
                    {
                        throw new ArgumentException("Unsupported transaction coordinate reference system.");
                    }

                    // WFS 1.0 coordinates use x/y, including QGIS's urn-form labels.
                    srs.Value = "EPSG:" + crs.Srid.ToString(CultureInfo.InvariantCulture);
                }
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
                throw new NotSupportedException("WFS 1.0 transaction geometries currently require two dimensions.");
            }

            if (element.Name.LocalName == "coordinates")
            {
                var tuples = SplitLegacyCoordinateTuples(element.Value, element.Attribute("ts")?.Value ?? " ");
                var separator = element.Attribute("cs")?.Value ?? ",";
                if (tuples.Any(tuple => tuple.Split(separator, StringSplitOptions.TrimEntries).Length != 2))
                {
                    throw new NotSupportedException("WFS 1.0 transaction geometries currently require two dimensions.");
                }
            }
            else if (element.Name.LocalName == "pos" &&
                     element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length != 2)
            {
                throw new NotSupportedException("WFS 1.0 transaction geometries currently require two dimensions.");
            }
        }
    }

    internal static string FormatWfs10TransactionResponse(string canonicalResponse)
    {
        XNamespace wfs = LegacyWfsNamespace;
        XNamespace ogc = OgcFilterNamespace;
        XNamespace wfs20 = Wfs20Utilities.WfsNamespace;
        XNamespace fes = Wfs20Utilities.FesNamespace;
        XNamespace honua = FeatureNamespaceUri;
        var canonical = XElement.Parse(canonicalResponse);
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
