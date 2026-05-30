// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, StoredQueryDefinition> ManagedStoredQueries =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IResult> HandleListStoredQueriesAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        // WFS ListStoredQueries is XML-only; don't 406 on `Accept: application/json`
        // default headers. The response is always application/xml.

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var xml = BuildListStoredQueriesXml(
            descriptors,
            ManagedStoredQueries.Values.OrderBy(query => query.Id, StringComparer.Ordinal).ToArray());
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
            if (!IsGetFeatureByIdStoredQuery(requestedId) &&
                !ManagedStoredQueries.ContainsKey(requestedId))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Stored query '{requestedId}' is not supported.",
                    "storedquery_id");
            }
        }

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var requestedDefinitions = requestedIds.Length == 0
            ? ManagedStoredQueries.Values.OrderBy(query => query.Id, StringComparer.Ordinal).ToArray()
            : requestedIds
                .Where(id => ManagedStoredQueries.TryGetValue(id, out _))
                .Select(id => ManagedStoredQueries[id])
                .ToArray();
        var includeBuiltIn = requestedIds.Length == 0 || requestedIds.Any(IsGetFeatureByIdStoredQuery);
        var xml = BuildDescribeStoredQueriesXml(descriptors, requestedDefinitions, includeBuiltIn);
        return Results.Content(xml, "application/xml", Encoding.UTF8);
    }


    public static IResult HandleCreateStoredQuery(HttpContext context)
    {
        var document = GetParsedStoredQueryRequestDocument(context);
        if (document?.Root is null ||
            !string.Equals(document.Root.Name.LocalName, Wfs20Utilities.Operations.CreateStoredQuery, StringComparison.OrdinalIgnoreCase))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "OperationParsingFailed",
                "CreateStoredQuery requires a wfs:CreateStoredQuery XML request body.",
                "request");
        }

        var definitionElement = document.Root.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "StoredQueryDefinition", StringComparison.OrdinalIgnoreCase));
        if (definitionElement is null)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "CreateStoredQuery requires a StoredQueryDefinition element.",
                "StoredQueryDefinition");
        }

        var id = definitionElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "StoredQueryDefinition requires an id attribute.",
                "id");
        }

        if (IsGetFeatureByIdStoredQuery(id) || ManagedStoredQueries.ContainsKey(id))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "DuplicateStoredQueryIdValue",
                $"Stored query '{id}' already exists.",
                id);
        }

        var queryExpressionText = definitionElement.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "QueryExpressionText", StringComparison.OrdinalIgnoreCase));
        if (queryExpressionText is null)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "StoredQueryDefinition requires a QueryExpressionText element.",
                "QueryExpressionText");
        }

        var language = queryExpressionText.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "language", StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
        if (!IsSupportedStoredQueryLanguage(language))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Unsupported stored query language '{language}'.",
                "language");
        }

        var query = queryExpressionText.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Query", StringComparison.OrdinalIgnoreCase));
        if (query is null)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "QueryExpressionText requires a Query element.",
                "Query");
        }

        var parameters = definitionElement.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Parameter", StringComparison.OrdinalIgnoreCase))
            .Select(element => new StoredQueryParameter(
                element.Attributes()
                    .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    .Trim() ?? string.Empty,
                element.Attributes()
                    .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "type", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    .Trim() ?? "xsd:string"))
            .Where(parameter => parameter.Name.Length > 0)
            .ToArray();
        var title = definitionElement.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Title", StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
        var abstractText = definitionElement.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Abstract", StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
        var returnFeatureTypes = queryExpressionText.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "returnFeatureTypes", StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim() ?? string.Empty;
        var queryTypeNames = query.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "typeNames", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attribute.Name.LocalName, "typeName", StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
        var filterXml = query.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase))
            ?.ToString(SaveOptions.DisableFormatting);

        var definition = new StoredQueryDefinition(
            id,
            title,
            abstractText,
            returnFeatureTypes,
            language!,
            queryTypeNames,
            filterXml,
            parameters);

        if (!ManagedStoredQueries.TryAdd(id, definition))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "DuplicateStoredQueryIdValue",
                $"Stored query '{id}' already exists.",
                id);
        }

        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "CreateStoredQueryResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("status", "OK");
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
        return Results.Content(xml, "application/xml", Encoding.UTF8);
    }


    public static IResult HandleDropStoredQuery(
        HttpContext context,
        string? storedQueryId)
    {
        if (string.IsNullOrWhiteSpace(storedQueryId))
        {
            storedQueryId = GetParsedStoredQueryRequestDocument(context)
                ?.Root
                ?.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase))
                ?.Value
                .Trim();
        }

        if (string.IsNullOrWhiteSpace(storedQueryId))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "DropStoredQuery requires a stored query identifier.",
                "storedquery_id");
        }

        if (IsGetFeatureByIdStoredQuery(storedQueryId) ||
            !ManagedStoredQueries.TryRemove(storedQueryId, out _))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Stored query '{storedQueryId}' does not exist.",
                "storedquery_id");
        }

        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "DropStoredQueryResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("status", "OK");
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
        return Results.Content(xml, "application/xml", Encoding.UTF8);
    }


    public async Task<IResult> HandleStoredQueryGetFeatureAsync(
        HttpContext context,
        string storedQueryId,
        string? featureId,
        string? typeNames,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken = default)
    {
        if (!IsGetFeatureByIdStoredQuery(storedQueryId))
        {
            if (ManagedStoredQueries.TryGetValue(storedQueryId, out var definition))
            {
                return await HandleManagedStoredQueryGetFeatureAsync(
                    context,
                    definition,
                    typeNames,
                    outputFormat,
                    count,
                    cancellationToken).ConfigureAwait(false);
            }

            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
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
            descriptor,
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
            descriptor.StorageLayerId,
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


    private async Task<IResult> HandleManagedStoredQueryGetFeatureAsync(
        HttpContext context,
        StoredQueryDefinition definition,
        string? typeNames,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken)
    {
        var resolvedTypeNames = ResolveManagedStoredQueryTypeNames(definition, typeNames);
        if (string.IsNullOrWhiteSpace(resolvedTypeNames))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                $"Stored query '{definition.Id}' requires a typeName parameter.",
                "typeName");
        }

        var resolvedFilter = ResolveManagedStoredQueryFilter(definition, context);
        return await HandleGetFeatureAsync(
            context,
            resolvedTypeNames,
            outputFormat,
            count,
            startIndex: null,
            sortBy: null,
            bbox: null,
            filter: resolvedFilter,
            resourceId: null,
            propertyName: null,
            srsName: null,
            resultType: null,
            cancellationToken).ConfigureAwait(false);
    }


    private static string? ResolveManagedStoredQueryTypeNames(StoredQueryDefinition definition, string? typeNames)
    {
        if (string.IsNullOrWhiteSpace(definition.QueryTypeNames))
        {
            return typeNames;
        }

        if (definition.QueryTypeNames.Contains("${typeName}", StringComparison.OrdinalIgnoreCase))
        {
            return typeNames;
        }

        return definition.QueryTypeNames;
    }


    private static string? ResolveManagedStoredQueryFilter(StoredQueryDefinition definition, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(definition.FilterXml))
        {
            return null;
        }

        var filter = definition.FilterXml;
        foreach (var parameter in definition.Parameters)
        {
            var value = context.Request.Query[parameter.Name].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                filter = filter.Replace("${" + parameter.Name + "}", SecurityElement.Escape(value), StringComparison.Ordinal);
            }
        }

        return filter;
    }


    private static string BuildListStoredQueriesXml(
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        IReadOnlyList<StoredQueryDefinition> managedQueries)
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

            foreach (var query in managedQueries)
            {
                writer.WriteStartElement("wfs", "StoredQuery", Wfs20Utilities.WfsNamespace);
                writer.WriteAttributeString("id", query.Id);
                WriteStoredQueryTitle(writer, query.Title ?? query.Id);
                foreach (var returnFeatureType in ParseReturnFeatureTypes(query.ReturnFeatureTypes, descriptors))
                {
                    writer.WriteElementString("wfs", "ReturnFeatureType", Wfs20Utilities.WfsNamespace, returnFeatureType);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
    }


    private static string BuildDescribeStoredQueriesXml(
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        IReadOnlyList<StoredQueryDefinition> managedQueries,
        bool includeBuiltIn)
    {
        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "DescribeStoredQueriesResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "fes", null, Wfs20Utilities.FesNamespace);
            writer.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);

            if (includeBuiltIn)
            {
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
                    writer.WriteAttributeString("language", WfsQueryExpressionLanguage);
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
            }

            foreach (var query in managedQueries)
            {
                writer.WriteStartElement("wfs", "StoredQueryDescription", Wfs20Utilities.WfsNamespace);
                writer.WriteAttributeString("id", query.Id);
                WriteStoredQueryTitle(writer, query.Title ?? query.Id);
                if (!string.IsNullOrWhiteSpace(query.Abstract))
                {
                    WriteStoredQueryAbstract(writer, query.Abstract);
                }

                foreach (var parameter in query.Parameters)
                {
                    writer.WriteStartElement("wfs", "Parameter", Wfs20Utilities.WfsNamespace);
                    writer.WriteAttributeString("name", parameter.Name);
                    writer.WriteAttributeString("type", parameter.Type);
                    writer.WriteEndElement();
                }

                writer.WriteStartElement("wfs", "QueryExpressionText", Wfs20Utilities.WfsNamespace);
                writer.WriteAttributeString("returnFeatureTypes", query.ReturnFeatureTypes);
                writer.WriteAttributeString("language", query.Language);
                writer.WriteAttributeString("isPrivate", XmlConvert.ToString(false));
                writer.WriteStartElement("wfs", "Query", Wfs20Utilities.WfsNamespace);
                if (!string.IsNullOrWhiteSpace(query.QueryTypeNames))
                {
                    writer.WriteAttributeString("typeNames", query.QueryTypeNames);
                }

                if (!string.IsNullOrWhiteSpace(query.FilterXml))
                {
                    WriteRawXmlFragment(writer, query.FilterXml);
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

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


    private static bool IsSupportedStoredQueryLanguage(string? language)
        => string.Equals(language, WfsQueryExpressionLanguage, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(language, LegacyWfsQueryExpressionLanguage, StringComparison.OrdinalIgnoreCase);


    private static XDocument? GetParsedStoredQueryRequestDocument(HttpContext context)
        => context.Items.TryGetValue(Wfs20DispatcherEndpoint.ParsedXmlDocumentItemKey, out var value) &&
           value is XDocument document
            ? document
            : null;


    private static string[] ParseReturnFeatureTypes(
        string returnFeatureTypes,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors)
    {
        var values = returnFeatureTypes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length > 0)
        {
            return values;
        }

        return descriptors.Select(descriptor => descriptor.QualifiedName).ToArray();
    }


    private static void WriteRawXmlFragment(XmlWriter writer, string xml)
    {
        using var reader = XmlReader.Create(
            new StringReader(xml),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
        writer.WriteNode(reader, false);
    }

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

    private sealed record StoredQueryDefinition(
        string Id,
        string? Title,
        string? Abstract,
        string ReturnFeatureTypes,
        string Language,
        string? QueryTypeNames,
        string? FilterXml,
        IReadOnlyList<StoredQueryParameter> Parameters);

    private sealed record StoredQueryParameter(string Name, string Type);

}
