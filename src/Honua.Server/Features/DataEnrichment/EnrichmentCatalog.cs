// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.DataEnrichment.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Resolves registered enrichment datasets by key (#374). Wraps the
/// configuration-bound <see cref="EnrichmentCatalogOptions"/> in a
/// case-insensitive lookup so the enrichment endpoints can translate a stable
/// caller-facing <c>datasetKey</c> into the underlying reference layer and its
/// default spatial-join behavior.
/// </summary>
internal sealed class EnrichmentCatalog
{
    private readonly FrozenDictionary<string, EnrichmentDatasetOptions> _byKey;

    public EnrichmentCatalog(IOptions<EnrichmentCatalogOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Last write wins on duplicate keys; operators are expected to keep keys
        // unique, but a deterministic resolution avoids a startup throw on a
        // misconfigured catalog.
        var map = new Dictionary<string, EnrichmentDatasetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataset in options.Value.Datasets)
        {
            if (string.IsNullOrWhiteSpace(dataset.Key))
            {
                continue;
            }

            map[dataset.Key] = dataset;
        }

        _byKey = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All registered datasets, ordered by key for stable catalog listings.
    /// </summary>
    public IReadOnlyList<EnrichmentDatasetOptions> Datasets =>
        _byKey.Values.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Resolves a dataset by its case-insensitive key. Returns null when no
    /// matching dataset is registered.
    /// </summary>
    public EnrichmentDatasetOptions? Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return _byKey.TryGetValue(key, out var dataset) ? dataset : null;
    }
}
