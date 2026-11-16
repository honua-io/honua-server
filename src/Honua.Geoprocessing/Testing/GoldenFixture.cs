// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.ObjectModel;

namespace Honua.Geoprocessing.Testing;

public sealed class GoldenFixture
{
    public GoldenFixture(
        string id,
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        string goldenPath,
        GoldenComparisonMode mode = GoldenComparisonMode.Auto,
        GoldenTolerance? tolerance = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(goldenPath);

        Id = id;
        ProcessId = processId;
        Inputs = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(inputs, StringComparer.Ordinal));
        GoldenPath = goldenPath;
        Mode = mode;
        Tolerance = tolerance ?? GoldenTolerance.Default;
        Description = description;
    }

    public string Id { get; }

    public string ProcessId { get; }

    public IReadOnlyDictionary<string, string> Inputs { get; }

    public string GoldenPath { get; }

    public GoldenComparisonMode Mode { get; }

    public GoldenTolerance Tolerance { get; }

    public string? Description { get; }
}
