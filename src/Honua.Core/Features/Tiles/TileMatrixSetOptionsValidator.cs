// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Honua.Core.Configuration;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Validates operator-defined custom tile matrix sets at startup. Rejects duplicate or
/// reserved-id collisions, non-monotonic scale denominators, non-positive matrix dimensions,
/// invalid SRIDs, and malformed top-left corners so a misconfigured gridset fails fast instead
/// of producing inconsistent capabilities.
/// </summary>
public sealed class TileMatrixSetOptionsValidator : OptionsValidator<TileMatrixSetDefinitionOptions>
{
    /// <inheritdoc />
    protected override void ValidateOptions(TileMatrixSetDefinitionOptions options, List<string> failures)
    {
        if (options.Custom is null || options.Custom.Count == 0)
        {
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < options.Custom.Count; index++)
        {
            var entry = options.Custom[index];
            var label = string.IsNullOrWhiteSpace(entry.Id)
                ? $"TileMatrixSets:Custom[{index}]"
                : $"TileMatrixSets:Custom '{entry.Id}'";

            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                failures.Add($"{label} must have a non-empty Id.");
            }
            else
            {
                foreach (var reserved in TileMatrixSetRegistry.ReservedIds)
                {
                    if (string.Equals(reserved, entry.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{label} collides with the reserved built-in tile matrix set '{reserved}'.");
                    }
                }

                if (!seenIds.Add(entry.Id))
                {
                    failures.Add($"{label} is a duplicate tile matrix set identifier.");
                }
            }

            if (string.IsNullOrWhiteSpace(entry.Crs))
            {
                failures.Add($"{label} must specify a Crs.");
            }

            if (entry.Srid <= 0)
            {
                failures.Add($"{label} must specify a positive Srid.");
            }

            if (entry.TileWidth <= 0 || entry.TileHeight <= 0)
            {
                failures.Add($"{label} must have positive TileWidth and TileHeight.");
            }

            if (entry.TopLeftCorner is null || entry.TopLeftCorner.Length != 2)
            {
                failures.Add($"{label} must specify a TopLeftCorner with exactly two values [x, y].");
            }

            ValidateLevels(entry, label, failures);
        }
    }

    private static void ValidateLevels(CustomTileMatrixSet entry, string label, List<string> failures)
    {
        if (entry.Levels is null || entry.Levels.Count == 0)
        {
            failures.Add($"{label} must define at least one level.");
            return;
        }

        var seenLevelIds = new HashSet<int>();
        double? previousScaleDenominator = null;
        var sortedLevels = new List<TileMatrixLevel>(entry.Levels);
        sortedLevels.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        foreach (var level in sortedLevels)
        {
            if (!seenLevelIds.Add(level.Id))
            {
                failures.Add($"{label} has a duplicate level id {level.Id}.");
            }

            if (level.ScaleDenominator <= 0)
            {
                failures.Add($"{label} level {level.Id} must have a positive ScaleDenominator.");
            }

            if (level.CellSize <= 0)
            {
                failures.Add($"{label} level {level.Id} must have a positive CellSize.");
            }

            if (level.MatrixWidth <= 0 || level.MatrixHeight <= 0)
            {
                failures.Add($"{label} level {level.Id} must have positive MatrixWidth and MatrixHeight.");
            }

            // Scale denominators must strictly decrease as the level (zoom) increases — each
            // level halves the cell size of the previous one. A non-monotonic set is a
            // misconfiguration that would corrupt zoom selection.
            if (previousScaleDenominator is { } previous && level.ScaleDenominator >= previous)
            {
                failures.Add(
                    $"{label} level {level.Id} ScaleDenominator must be strictly smaller than the previous level (scale denominators must decrease as level increases).");
            }

            if (level.ScaleDenominator > 0)
            {
                previousScaleDenominator = level.ScaleDenominator;
            }
        }
    }
}
