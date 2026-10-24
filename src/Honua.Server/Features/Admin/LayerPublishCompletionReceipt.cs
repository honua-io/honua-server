// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin;

/// <summary>Committed publication result, independent of the best-effort progress projection.</summary>
internal static class LayerPublishCompletionReceipt
{
    private const string Prefix = "layer-publish:";

    internal static string Create(
        int layerId,
        (long RowsMaterialized, long RowsTotal)? snapshot,
        long? metadataRevision = null)
    {
        var value = snapshot is { } observed
            ? FormattableString.Invariant($"{Prefix}{layerId}:{observed.RowsMaterialized}:{observed.RowsTotal}")
            : Prefix + layerId.ToString(CultureInfo.InvariantCulture);
        if (metadataRevision is not > 0)
        {
            return value;
        }

        // A layer-only receipt with a revision needs a marker: an older
        // two-field numeric receipt was malformed and must stay invalid.
        return snapshot is null
            ? value + ":r" + metadataRevision.Value.ToString(CultureInfo.InvariantCulture)
            : value + ":" + metadataRevision.Value.ToString(CultureInfo.InvariantCulture);
    }

    internal static (int LayerId, long? RowsMaterialized, long? RowsTotal, long? MetadataRevision)? Read(
        ExecutionJobRecord job)
    {
        (int LayerId, long? RowsMaterialized, long? RowsTotal, long? MetadataRevision)? legacy = null;
        foreach (var reference in job.CommittedEffectReferences.Concat(job.ArtifactReferences))
        {
            if (!reference.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = reference[Prefix.Length..].Split(':');
            if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var layerId) || layerId <= 0)
            {
                continue;
            }

            if (fields.Length == 1)
            {
                // Older workers recorded only the layer id. Do not infer unrecorded row counts.
                legacy ??= (layerId, null, null, null);
            }
            else if (fields.Length == 2 &&
                fields[1].StartsWith('r') &&
                long.TryParse(fields[1].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) &&
                revision > 0)
            {
                legacy = (layerId, null, null, revision);
            }
            else if ((fields.Length == 3 || fields.Length == 4)
                && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var materialized)
                && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                && materialized >= 0 && total >= materialized)
            {
                if (fields.Length == 4)
                {
                    if (!long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var richRevision) ||
                        richRevision <= 0)
                    {
                        continue;
                    }

                    return (layerId, materialized, total, richRevision);
                }

                return (layerId, materialized, total, null);
            }
        }

        return legacy;
    }
}
