// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalVectorEnvelopeReader
{
    internal readonly record struct Envelope(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;

        public double Height => MaxY - MinY;
    }

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
