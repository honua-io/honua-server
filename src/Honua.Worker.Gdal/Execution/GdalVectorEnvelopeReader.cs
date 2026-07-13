// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Cheap, dependency-free GeoJSON envelope reader used to bound the caller-controlled
/// output RESOLUTION of <c>conversion.rasterize</c> (#2793). <c>gdal_rasterize -tr</c>
/// derives the output pixel grid from the INPUT LAYER extent ÷ target cell size, and
/// the input layer is the untrusted base64 GeoJSON payload — so a tiny cell size over a
/// wide envelope yields billions of output pixels. This reader scans the payload's
/// <c>coordinates</c> (and any declared <c>bbox</c>) once with a forward-only
/// <see cref="Utf8JsonReader"/> — no geometry objects allocated, no GDAL — to recover the
/// axis-aligned extent that <see cref="GdalOutputGridGuard.TryAdmitResolution"/> then
/// bounds before the subprocess spawns.
/// </summary>
internal static class GdalVectorEnvelopeReader
{
    /// <summary>Axis-aligned bounds recovered from a GeoJSON payload.</summary>
    /// <param name="MinX">Minimum X / longitude.</param>
    /// <param name="MinY">Minimum Y / latitude.</param>
    /// <param name="MaxX">Maximum X / longitude.</param>
    /// <param name="MaxY">Maximum Y / latitude.</param>
    internal readonly record struct Envelope(double MinX, double MinY, double MaxX, double MaxY)
    {
        /// <summary>Extent along X (<c>MaxX - MinX</c>).</summary>
        public double Width => MaxX - MinX;

        /// <summary>Extent along Y (<c>MaxY - MinY</c>).</summary>
        public double Height => MaxY - MinY;
    }

    /// <summary>
    /// Reads the axis-aligned envelope spanning every coordinate position (and any
    /// <c>bbox</c>) in a GeoJSON payload. Returns <c>false</c> — leaving the job to be
    /// admitted and bounded only by the input/timeout backstop — when the payload holds
    /// no readable positions or is not parseable as JSON (GDAL then adjudicates the same
    /// bytes). Only the first two ordinates of each position (X, Y) are considered;
    /// elevation and any non-finite ordinate are ignored. Feature attribute payloads are
    /// excluded: a <c>properties</c> member's entire subtree is skipped, so ordinary
    /// attributes named <c>coordinates</c> or <c>bbox</c> never contribute to the envelope
    /// (GeoJSON geometry members can never legally live under <c>properties</c>).
    /// </summary>
    public static bool TryReadEnvelope(ReadOnlySpan<byte> geojson, out Envelope envelope)
    {
        envelope = default;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var found = false;

        try
        {
            var reader = new Utf8JsonReader(geojson, isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("properties"))
                {
                    // Skip the feature-attribute subtree wholesale so attribute keys that
                    // happen to be named "coordinates"/"bbox" cannot pollute the envelope.
                    reader.Read();
                    reader.Skip();
                }
                else if (reader.ValueTextEquals("coordinates"))
                {
                    found |= ScanCoordinates(ref reader, ref minX, ref minY, ref maxX, ref maxY);
                }
                else if (reader.ValueTextEquals("bbox"))
                {
                    found |= ScanBoundingBox(ref reader, ref minX, ref minY, ref maxX, ref maxY);
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (!found || minX > maxX || minY > maxY)
        {
            return false;
        }

        envelope = new Envelope(minX, minY, maxX, maxY);
        return true;
    }

    /// <summary>
    /// Walks the array subtree following a <c>coordinates</c> property, accumulating the
    /// min/max of the first two ordinates of every innermost position array. Handles any
    /// GeoJSON geometry (Point through MultiPolygon / GeometryCollection nesting) because
    /// the ordinate index resets at each array boundary.
    /// </summary>
    private static bool ScanCoordinates(ref Utf8JsonReader reader, ref double minX, ref double minY, ref double maxX, ref double maxY)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        var depth = 1;
        var ordinalIndex = 0;
        var updated = false;
        double x = 0;

        while (depth > 0 && reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    depth++;
                    ordinalIndex = 0;
                    break;
                case JsonTokenType.EndArray:
                    depth--;
                    break;
                case JsonTokenType.Number:
                    if (ordinalIndex == 0)
                    {
                        x = reader.GetDouble();
                    }
                    else if (ordinalIndex == 1)
                    {
                        var y = reader.GetDouble();
                        if (double.IsFinite(x) && double.IsFinite(y))
                        {
                            Extend(ref minX, ref maxX, x);
                            Extend(ref minY, ref maxY, y);
                            updated = true;
                        }
                    }
                    ordinalIndex++;
                    break;
                default:
                    // Strings / null inside a coordinates array are malformed; ignore.
                    break;
            }
        }

        return updated;
    }

    /// <summary>
    /// Reads a GeoJSON <c>bbox</c> array (<c>[minX, minY, maxX, maxY]</c> or the
    /// 3D <c>[minX, minY, minZ, maxX, maxY, maxZ]</c> form) and folds it into the running
    /// envelope. Any other arity is ignored.
    /// </summary>
    private static bool ScanBoundingBox(ref Utf8JsonReader reader, ref double minX, ref double minY, ref double maxX, ref double maxY)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        Span<double> values = stackalloc double[6];
        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number)
            {
                return false;
            }
            if (count >= values.Length)
            {
                return false;
            }
            values[count++] = reader.GetDouble();
        }

        (double bMinX, double bMinY, double bMaxX, double bMaxY) = count switch
        {
            4 => (values[0], values[1], values[2], values[3]),
            6 => (values[0], values[1], values[3], values[4]),
            _ => (double.NaN, double.NaN, double.NaN, double.NaN),
        };

        if (!double.IsFinite(bMinX) || !double.IsFinite(bMinY) || !double.IsFinite(bMaxX) || !double.IsFinite(bMaxY))
        {
            return false;
        }

        Extend(ref minX, ref maxX, bMinX);
        Extend(ref minX, ref maxX, bMaxX);
        Extend(ref minY, ref maxY, bMinY);
        Extend(ref minY, ref maxY, bMaxY);
        return true;
    }

    private static void Extend(ref double min, ref double max, double value)
    {
        if (value < min)
        {
            min = value;
        }
        if (value > max)
        {
            max = value;
        }
    }
}
