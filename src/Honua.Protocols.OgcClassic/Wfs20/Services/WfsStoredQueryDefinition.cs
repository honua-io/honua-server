// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// A managed WFS 2.0 stored query created via <c>CreateStoredQuery</c>.
/// Persisted through <see cref="IWfsStoredQueryStore"/> so that all replicas
/// observe the same set of managed stored queries.
/// </summary>
/// <param name="Id">The unique stored query identifier (case-insensitive).</param>
/// <param name="Title">Optional human-readable title.</param>
/// <param name="Abstract">Optional human-readable abstract.</param>
/// <param name="ReturnFeatureTypes">
/// The raw <c>returnFeatureTypes</c> attribute value from the query expression text
/// (space- or comma-separated qualified feature type names; may be empty).
/// </param>
/// <param name="Language">The declared query expression language URN.</param>
/// <param name="QueryTypeNames">Optional <c>typeNames</c> attribute captured from the query element.</param>
/// <param name="FilterXml">Optional serialized <c>fes:Filter</c> fragment captured from the query element.</param>
/// <param name="Parameters">The declared stored query parameters (possibly empty).</param>
internal sealed record WfsStoredQueryDefinition(
    string Id,
    string? Title,
    string? Abstract,
    string ReturnFeatureTypes,
    string Language,
    string? QueryTypeNames,
    string? FilterXml,
    IReadOnlyList<WfsStoredQueryParameter> Parameters);

/// <summary>
/// A declared parameter of a managed WFS 2.0 stored query.
/// </summary>
/// <param name="Name">The parameter name referenced as <c>${name}</c> in the filter template.</param>
/// <param name="Type">The declared XSD type (for example <c>xsd:string</c>).</param>
internal sealed record WfsStoredQueryParameter(string Name, string Type);
