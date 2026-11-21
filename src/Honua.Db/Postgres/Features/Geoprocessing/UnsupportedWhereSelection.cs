// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.Geoprocessing;

/// <summary>
/// Geoprocessing reads reject an unparseable legacy <c>where</c> as a bare
/// <see cref="ArgumentException"/> whose text may quote the clause. Executors surface
/// <see cref="DagSourceSelectionException"/> verbatim as a non-retryable refusal, so this
/// translates that one caller error into fixed guidance the caller can act on.
/// </summary>
internal static class UnsupportedWhereSelection
{
    internal const string Message =
        "the where clause is not supported; use simple comparisons joined by AND, such as name = 'value' or age > 18";

    internal static async IAsyncEnumerable<Feature> Explain(
        IAsyncEnumerable<Feature> rows,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (ArgumentException ex) when (IsUnsupported(query))
                {
                    throw new DagSourceSelectionException(Message, ex);
                }

                if (!moved)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    // Only a clause the legacy grammar itself rejects is relabelled; any other store
    // argument failure keeps its type and existing classification.
    private static bool IsUnsupported(FeatureQuery query)
        => query.SqlFilter is null
            && !string.IsNullOrWhiteSpace(query.Where)
            && !FeatureQueryBuilder.IsSupportedWhereClause(query.Where);
}
