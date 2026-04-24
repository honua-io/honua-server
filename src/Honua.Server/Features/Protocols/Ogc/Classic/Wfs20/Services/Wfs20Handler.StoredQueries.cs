// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.ServiceDefaults;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Core handler for WFS 2.0 operations backed by the shared catalog and feature stores.
/// </summary>
internal sealed partial class Wfs20Handler
{

    public async Task<IResult> HandleListStoredQueriesAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        // WFS ListStoredQueries is XML-only; don't 406 on `Accept: application/json`
        // default headers. The response is always application/xml.

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var xml = BuildListStoredQueriesXml(descriptors);
        return Results.Content(xml, "application/xml", Encoding.UTF8);
    }


    public async Task<IResult> HandleDescribeStoredQueriesAsync(
        HttpContext context,
        string? storedQueryIds,
        CancellationToken cancellationToken = default)
    {
        // Same story — DescribeStoredQueries is XML-only.

        var requestedIds = ParseQualifiedList(storedQueryIds);
        foreach (var requestedId in requestedIds)
        {
            if (!IsGetFeatureByIdStoredQuery(requestedId))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Stored query '{requestedId}' is not supported.",
                    "storedquery_id");
            }
        }

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var xml = BuildDescribeStoredQueriesXml(descriptors);
        return Results.Content(xml, "application/xml", Encoding.UTF8);
    }


    public async Task<IResult> HandleStoredQueryGetFeatureAsync(
        HttpContext context,
        string storedQueryId,
        string? featureId,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken = default)
    {
        if (!IsGetFeatureByIdStoredQuery(storedQueryId))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "OperationParsingFailed",
                $"Stored query '{storedQueryId}' is not supported.",
                "storedquery_id");
        }

        if (string.IsNullOrWhiteSpace(featureId))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Stored query 'GetFeatureById' requires an 'id' parameter.",
                "id");
        }

        var normalizedFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        if (!string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Stored query 'GetFeatureById' supports only '{Wfs20Utilities.OutputFormats.Gml32}'.",
                "outputFormat");
        }

        if (!Wfs20Utilities.TryParseCount(count, out _))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid COUNT parameter '{count}'. COUNT must be a non-negative integer.",
                "count");
        }

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var descriptor = ResolveStoredQueryFeatureTypeDescriptor(descriptors, featureId);
        if (descriptor is null)
        {
            return CreateStoredQueryFeatureNotFoundResult(context, featureId);
        }

        var query = await BuildFeatureQueryAsync(
            descriptor.Layer,
            propertyName: null,
            sortBy: null,
            bbox: null,
            filter: null,
            resourceId: featureId,
            srsName: null,
            enforceResourceIdTypeMatch: true,
            requireResourceIdQualifier: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = await _gmlFeatureStore.QueryGmlAsync(
            descriptor.Layer.Id,
            query with { Limit = 1 },
            cancellationToken).ConfigureAwait(false);
        if (result.Items.IsDefaultOrEmpty)
        {
            return CreateStoredQueryFeatureNotFoundResult(context, featureId);
        }

        var plan = new LayerQueryPlan(descriptor, query with { Limit = 1 }, 1);
        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            WriteFeature(writer, plan, result.Items[0], includeMemberWrapper: false);
            writer.WriteEndDocument();
        });

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }


    private static string BuildListStoredQueriesXml(IReadOnlyList<WfsFeatureTypeDescriptor> descriptors)
    {
        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "ListStoredQueriesResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);

            writer.WriteStartElement("wfs", "StoredQuery", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("id", GetFeatureByIdStoredQueryId);
            WriteStoredQueryTitle(writer, "Get feature by identifier");

            foreach (var descriptor in descriptors)
            {
                writer.WriteElementString("wfs", "ReturnFeatureType", Wfs20Utilities.WfsNamespace, descriptor.QualifiedName);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
    }


    private static string BuildDescribeStoredQueriesXml(IReadOnlyList<WfsFeatureTypeDescriptor> descriptors)
    {
        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "DescribeStoredQueriesResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "fes", null, Wfs20Utilities.FesNamespace);
            writer.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);

            writer.WriteStartElement("wfs", "StoredQueryDescription", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("id", GetFeatureByIdStoredQueryId);
            WriteStoredQueryTitle(writer, "Get feature by identifier");
            WriteStoredQueryAbstract(writer, "Returns a single feature that matches the supplied identifier.");
            writer.WriteStartElement("wfs", "Parameter", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("name", "id");
            writer.WriteAttributeString("type", "xsd:string");
            writer.WriteEndElement();

            foreach (var descriptor in descriptors)
            {
                writer.WriteStartElement("wfs", "QueryExpressionText", Wfs20Utilities.WfsNamespace);
                writer.WriteAttributeString("returnFeatureTypes", descriptor.QualifiedName);
                writer.WriteAttributeString("language", "urn:ogc:def:queryLanguage:OGC-WFS::WFS_QueryExpression");
                writer.WriteAttributeString("isPrivate", XmlConvert.ToString(false));

                writer.WriteStartElement("wfs", "Query", Wfs20Utilities.WfsNamespace);
                writer.WriteAttributeString("typeNames", descriptor.QualifiedName);
                writer.WriteStartElement("fes", "Filter", Wfs20Utilities.FesNamespace);
                writer.WriteStartElement("fes", "ResourceId", Wfs20Utilities.FesNamespace);
                writer.WriteAttributeString("rid", "${id}");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
    }


    private static void WriteStoredQueryTitle(XmlWriter writer, string value)
    {
        writer.WriteStartElement("wfs", "Title", Wfs20Utilities.WfsNamespace);
        writer.WriteAttributeString("xml", "lang", "http://www.w3.org/XML/1998/namespace", "en");
        writer.WriteString(value);
        writer.WriteEndElement();
    }


    private static void WriteStoredQueryAbstract(XmlWriter writer, string value)
    {
        writer.WriteStartElement("wfs", "Abstract", Wfs20Utilities.WfsNamespace);
        writer.WriteAttributeString("xml", "lang", "http://www.w3.org/XML/1998/namespace", "en");
        writer.WriteString(value);
        writer.WriteEndElement();
    }


    private static bool IsGetFeatureByIdStoredQuery(string storedQueryId)
        => string.Equals(storedQueryId, GetFeatureByIdStoredQueryId, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(storedQueryId, GetFeatureByIdStoredQueryUri, StringComparison.OrdinalIgnoreCase);

    private static WfsFeatureTypeDescriptor? ResolveStoredQueryFeatureTypeDescriptor(
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        string featureId)
    {
        var trimmedFeatureId = featureId.Trim();
        var lastDot = trimmedFeatureId.LastIndexOf('.');
        if (lastDot > 0)
        {
            var typeName = trimmedFeatureId[..lastDot];
            var localName = typeName.Contains(':', StringComparison.Ordinal)
                ? typeName[(typeName.LastIndexOf(':') + 1)..]
                : typeName;

            return descriptors.FirstOrDefault(descriptor =>
                descriptor.QualifiedName.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        }

        return descriptors.Count == 1
            ? descriptors[0]
            : null;
    }

}
