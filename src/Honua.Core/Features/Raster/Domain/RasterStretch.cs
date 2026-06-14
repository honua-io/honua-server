// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Display stretch algorithm applied to raster pixel values to produce
/// viewable 8-bit output. Mirrors the subset of the Esri ImageServer
/// <c>Stretch</c> raster function (<c>esriRasterStretchType</c>) that Honua
/// executes today. Protocol adapters translate the Esri integer
/// <c>StretchType</c> into these neutral members before threading a
/// <see cref="RasterStretch"/> onto a <see cref="RasterQuery"/>.
/// </summary>
public enum RasterStretchType
{
    /// <summary>
    /// Linear stretch between the band minimum and maximum
    /// (Esri <c>esriRasterStretch_MinimumMaximum</c> = 5).
    /// </summary>
    MinMax = 0,

    /// <summary>
    /// Linear stretch between <c>mean ± n·σ</c>
    /// (Esri <c>esriRasterStretch_StandardDeviations</c> = 3).
    /// </summary>
    StandardDeviation = 1,

    /// <summary>
    /// Linear stretch between the low and high histogram percentile cutoffs
    /// (Esri <c>esriRasterStretch_PercentMinimumMaximum</c> = 6).
    /// </summary>
    PercentClip = 2,
}

/// <summary>
/// Specification for a display stretch carried on a <see cref="RasterQuery"/>.
/// When present, the raster store linearly rescales each selected band to the
/// 8-bit display range using bounds derived from the chosen
/// <see cref="StretchType"/>. A <c>null</c> stretch on a query leaves pixel
/// values untouched.
/// </summary>
public readonly record struct RasterStretch
{
    /// <summary>
    /// Algorithm used to derive the low/high stretch bounds for each band.
    /// </summary>
    public required RasterStretchType StretchType { get; init; }

    /// <summary>
    /// Number of standard deviations from the mean used when
    /// <see cref="StretchType"/> is <see cref="RasterStretchType.StandardDeviation"/>.
    /// Defaults to the Esri convention of 2.5.
    /// </summary>
    public double NumberOfStandardDeviations { get; init; } = 2.5;

    /// <summary>
    /// Low-end clip percentage (0–100) used when <see cref="StretchType"/> is
    /// <see cref="RasterStretchType.PercentClip"/>. Defaults to 0.25.
    /// </summary>
    public double MinPercent { get; init; } = 0.25;

    /// <summary>
    /// High-end clip percentage (0–100) used when <see cref="StretchType"/> is
    /// <see cref="RasterStretchType.PercentClip"/>. Defaults to 0.25.
    /// </summary>
    public double MaxPercent { get; init; } = 0.25;

    /// <summary>
    /// Optional explicit per-band minimum values supplied by the rendering rule
    /// (Esri <c>Statistics</c>). When set alongside <see cref="StatisticsMax"/>,
    /// the store uses these instead of querying raster statistics.
    /// Index 0 corresponds to the first selected band.
    /// </summary>
    public double[]? StatisticsMin { get; init; }

    /// <summary>
    /// Optional explicit per-band maximum values supplied by the rendering rule
    /// (Esri <c>Statistics</c>). Paired with <see cref="StatisticsMin"/>.
    /// </summary>
    public double[]? StatisticsMax { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RasterStretch"/> struct.
    /// </summary>
    public RasterStretch()
    {
    }
}
