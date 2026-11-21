// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.Geoprocessing;

// Stores reject an unparseable legacy where as a bare ArgumentException whose text may
// quote the clause. Executors surface DagSourceSelectionException verbatim as a
// non-retryable refusal, so translate that one caller error into fixed guidance.
internal static class UnsupportedWhereSelection
{
    internal const string Message =
        "the where clause is not supported; use simple comparisons joined by AND, such as name = 'value' or age > 18";

    internal static async IAsyncEnumerable<Feature> Explain(
        IAsyncEnumerable<Feature> rows,
        string? callerWhere,
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
                catch (ArgumentException ex) when (IsUnsupported(callerWhere))
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

    // Only a clause the caller wrote and the legacy grammar rejects is relabelled; server-added
    // predicates (e.g. a watermark) and other store argument failures keep their classification.
    // Callers pass null when a structured SqlFilter replaces the legacy clause.
    private static bool IsUnsupported(string? callerWhere)
        => !string.IsNullOrWhiteSpace(callerWhere)
            && !FeatureQueryBuilder.IsSupportedWhereClause(callerWhere);
}
