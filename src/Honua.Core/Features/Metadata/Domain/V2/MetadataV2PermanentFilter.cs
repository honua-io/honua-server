// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Always-on filter applied to every query against the owning
/// <see cref="MetadataV2Resource"/>. Used to express row-level visibility
/// constraints declaratively at the metadata layer ("only show rows where
/// classification = 'public'", "only show rows within tenant N", …).
/// </summary>
/// <remarks>
/// Storage backends honour this by ANDing the parsed expression with the
/// per-request filter before SQL translation. The same v1 use cases — saved
/// filter shipped from admin UIs, tenant-scoped views, soft-delete masks —
/// continue to apply.
/// </remarks>
public sealed record MetadataV2PermanentFilter
{
    /// <summary>
    /// Filter source text in the declared <see cref="Language"/>. Empty or
    /// whitespace-only expressions are treated as "no filter".
    /// </summary>
    [JsonPropertyName("expression")]
    public string Expression { get; init; } = string.Empty;

    /// <summary>
    /// Identifier of the filter language. The canonical values mirror the v1
    /// <c>LayerPermanentFilterLanguages</c> constants:
    /// <list type="bullet">
    /// <item><c>arcgis-sql</c> — Esri SQL92 subset (default).</item>
    /// <item><c>cql2-text</c> — OGC CQL2 text encoding.</item>
    /// <item><c>cql2-json</c> — OGC CQL2 JSON encoding.</item>
    /// </list>
    /// Unknown languages cause the storage backend to throw at query time so
    /// misconfiguration surfaces immediately rather than silently bypassing
    /// the filter.
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; init; } = string.Empty;
}
