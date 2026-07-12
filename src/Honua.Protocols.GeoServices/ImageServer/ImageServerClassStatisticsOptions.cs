// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Admission limits for the ImageServer <c>computeClassStatistics</c> operation. These bound the
/// CPU and memory an unauthenticated class-signature request can consume so the operation is safe
/// to enable in a rolling deployment (per #2662).
/// </summary>
public sealed class ImageServerClassStatisticsOptions
{
    /// <summary>The configuration section name that binds to these options.</summary>
    public const string SectionName = "GeoServices:ImageServer:ClassStatistics";

    /// <summary>
    /// Maximum number of pixels (clip bounding box) analysed per class AOI before the request is
    /// rejected. Bounds the memory a single class signature can materialize. Defaults to 4,000,000
    /// (a 2000x2000 AOI).
    /// </summary>
    [Range(1, 100_000_000)]
    public int MaxPixelsPerClass { get; set; } = 4_000_000;

    /// <summary>
    /// Maximum number of classes accepted in one request. Bounds the total analysis work. Defaults
    /// to 64.
    /// </summary>
    [Range(1, 4096)]
    public int MaxClasses { get; set; } = 64;
}
