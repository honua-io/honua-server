// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin;

/// <summary>Committed publication result, independent of the best-effort progress projection.</summary>
internal static class LayerPublishCompletionReceipt
{
    private const string Prefix = "layer-publish:";

    internal static string Create(int layerId, (long RowsMaterialized, long RowsTotal)? snapshot)
        => snapshot is { } observed
            ? FormattableString.Invariant($"{Prefix}{layerId}:{observed.RowsMaterialized}:{observed.RowsTotal}")
            : Prefix + layerId.ToString(CultureInfo.InvariantCulture);

    internal static (int LayerId, long? RowsMaterialized, long? RowsTotal)? Read(ExecutionJobRecord job)
    {
        (int LayerId, long? RowsMaterialized, long? RowsTotal)? legacy = null;
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
                legacy ??= (layerId, null, null);
            }
            else if (fields.Length == 3
                && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var materialized)
                && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                && materialized >= 0 && total >= materialized)
            {
                return (layerId, materialized, total);
            }
        }

        return legacy;
    }
}
