// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Outcome classification for <see cref="InlineColorRamp.Resolve"/>.
/// </summary>
public enum InlineColorRampStatus
{
    /// <summary>The ramp was recognised and resolved to a colormap.</summary>
    Resolved,

    /// <summary>The ramp document is malformed or references an unknown type (HTTP 400).</summary>
    Invalid,

    /// <summary>The ramp form is recognised but not supported on this service (HTTP 501).</summary>
    Unsupported,
}

/// <summary>
/// Result of resolving an inline Esri <c>Colorramp</c> object into a canonical
/// <see cref="RasterColormap"/>.
/// </summary>
public readonly record struct InlineColorRampResolution(
    InlineColorRampStatus Status,
    RasterColormap? Colormap,
    string? Error)
{
    public static InlineColorRampResolution Resolved(RasterColormap colormap)
        => new(InlineColorRampStatus.Resolved, colormap, null);

    public static InlineColorRampResolution Invalid(string error)
        => new(InlineColorRampStatus.Invalid, null, error);

    public static InlineColorRampResolution Unsupported(string error)
        => new(InlineColorRampStatus.Unsupported, null, error);
}

/// <summary>
/// Resolves the supported subset of Esri inline <c>Colorramp</c> objects (the richer
/// algorithmic/multipart ramp definitions embedded in a <c>Colormap</c> raster function) into
/// the same canonical <see cref="RasterColormap"/> the named-ramp path (<see cref="NamedColorRamp"/>)
/// produces, so a resolved ramp flows through the shared render pipeline unchanged.
/// </summary>
/// <remarks>
/// <para>Supported forms (per Esri's ColorRamp specification):</para>
/// <list type="bullet">
/// <item><description>
/// <b>algorithmic</b> — <c>fromColor</c>/<c>toColor</c> RGB(A) stops interpolated with an
/// <c>algorithm</c> of <c>esriHSVAlgorithm</c>, <c>esriCIELabAlgorithm</c>, or
/// <c>esriLabLChAlgorithm</c> (the algorithm is applied when sampling the gradient so the
/// emitted stops follow the requested colour-space arc). When <c>algorithm</c> is omitted the
/// CIELAB path is used, matching the ArcGIS Pro default for continuous ramps.
/// </description></item>
/// <item><description>
/// <b>multipart</b> — a <c>colorRamps</c> array of algorithmic ramps laid end to end across
/// the display range in equal segments.
/// </description></item>
/// </list>
/// <para>
/// Every other form is rejected explicitly rather than silently ignored or approximated:
/// <c>random</c> ramps are rejected as unsupported (non-deterministic output), and unknown
/// types, missing colours, or malformed colour arrays are rejected as invalid.
/// </para>
/// <para>
/// The ramp is sampled at several intermediate stops per segment so the raster store's linear
/// RGB interpolation between stops faithfully reproduces the (potentially non-linear)
/// colour-space gradient. Stops are spread across the same 0..255 post-stretch display range as
/// the named-ramp path.
/// </para>
/// </remarks>
public static class InlineColorRamp
{
    // Esri authors ramps over the post-stretch 8-bit display range; mirror NamedColorRamp so a
    // Stretch -> Colormap chain renders the ramp end to end and named/inline ramps stay consistent.
    private const double RangeMin = 0;
    private const double RangeMax = 255;

    // Interior sample count per algorithmic segment (including both endpoints). The store
    // interpolates linearly in RGB between stops, so several samples keep an HSV/Lab arc faithful.
    private const int SamplesPerSegment = 9;

    /// <summary>Human-readable description of the supported inline ramp subset, for error messages.</summary>
    public static string SupportedFormsText =>
        "algorithmic (fromColor/toColor with algorithm esriHSVAlgorithm, esriCIELabAlgorithm, or esriLabLChAlgorithm) " +
        "and multipart (a colorRamps array of algorithmic ramps)";

    /// <summary>
    /// Resolves an inline <c>Colorramp</c> object into a <see cref="RasterColormap"/>.
    /// </summary>
    /// <param name="element">The parsed <c>Colorramp</c> JSON object.</param>
    /// <returns>
    /// A resolution whose <see cref="InlineColorRampResolution.Status"/> distinguishes a resolved
    /// colormap from an explicit invalid (400) or unsupported (501) rejection.
    /// </returns>
    public static InlineColorRampResolution Resolve(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return InlineColorRampResolution.Invalid(
                "Colorramp must be a JSON object with a 'type' of 'algorithmic' or 'multipart'.");
        }

        if (!TryGetString(element, "type", out var type))
        {
            return InlineColorRampResolution.Invalid(
                "Colorramp requires a 'type' of 'algorithmic' or 'multipart'.");
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "algorithmic" => ResolveAlgorithmic(element),
            "multipart" => ResolveMultipart(element),
            "random" => InlineColorRampResolution.Unsupported(
                "Colorramp type 'random' is not supported because it produces a non-deterministic colormap. " +
                $"Supported forms: {SupportedFormsText}."),
            _ => InlineColorRampResolution.Invalid(
                $"Colorramp type '{type}' is not recognized. Supported forms: {SupportedFormsText}."),
        };
    }

    private static InlineColorRampResolution ResolveAlgorithmic(JsonElement element)
    {
        if (!TryParseSegment(element, out var segment, out var error))
        {
            return error;
        }

        var samples = new List<(double Fraction, ColorRgba Color)>(SamplesPerSegment);
        AppendSegmentSamples(samples, segment, gStart: 0d, gEnd: 1d, includeStart: true);
        return InlineColorRampResolution.Resolved(BuildColormap(samples));
    }

    private static InlineColorRampResolution ResolveMultipart(JsonElement element)
    {
        if (!element.TryGetProperty("colorRamps", out var ramps) &&
            !element.TryGetProperty("colorramps", out ramps))
        {
            return InlineColorRampResolution.Invalid(
                "A multipart Colorramp requires a non-empty 'colorRamps' array of algorithmic ramps.");
        }

        if (ramps.ValueKind != JsonValueKind.Array || ramps.GetArrayLength() == 0)
        {
            return InlineColorRampResolution.Invalid(
                "A multipart Colorramp requires a non-empty 'colorRamps' array of algorithmic ramps.");
        }

        var segments = new List<Segment>(ramps.GetArrayLength());
        foreach (var child in ramps.EnumerateArray())
        {
            // Esri multipart ramps compose algorithmic parts only; reject nested multipart/random
            // rather than silently flattening an unsupported structure.
            if (!TryGetString(child, "type", out var childType) ||
                !string.Equals(childType.Trim(), "algorithmic", StringComparison.OrdinalIgnoreCase))
            {
                return InlineColorRampResolution.Invalid(
                    "Each entry of a multipart Colorramp's 'colorRamps' must be an algorithmic ramp.");
            }

            if (!TryParseSegment(child, out var segment, out var error))
            {
                return error;
            }

            segments.Add(segment);
        }

        // Lay the parts end to end across equal fractions of the display range. Skip the leading
        // sample of every part after the first so shared boundaries are not emitted twice.
        var samples = new List<(double Fraction, ColorRgba Color)>(segments.Count * SamplesPerSegment);
        for (var i = 0; i < segments.Count; i++)
        {
            var gStart = (double)i / segments.Count;
            var gEnd = (double)(i + 1) / segments.Count;
            AppendSegmentSamples(samples, segments[i], gStart, gEnd, includeStart: i == 0);
        }

        return InlineColorRampResolution.Resolved(BuildColormap(samples));
    }

    private static bool TryParseSegment(JsonElement element, out Segment segment, out InlineColorRampResolution error)
    {
        segment = default;
        error = default;

        if (!TryParseColor(element, "fromColor", out var from))
        {
            error = InlineColorRampResolution.Invalid(
                "An algorithmic Colorramp requires a 'fromColor' array of [r, g, b] (alpha optional) channels 0-255.");
            return false;
        }

        if (!TryParseColor(element, "toColor", out var to))
        {
            error = InlineColorRampResolution.Invalid(
                "An algorithmic Colorramp requires a 'toColor' array of [r, g, b] (alpha optional) channels 0-255.");
            return false;
        }

        var algorithm = RampAlgorithm.CieLab;
        if (TryGetString(element, "algorithm", out var algorithmName))
        {
            switch (algorithmName.Trim().ToLowerInvariant())
            {
                case "esrihsvalgorithm":
                    algorithm = RampAlgorithm.Hsv;
                    break;
                case "esricielabalgorithm":
                    algorithm = RampAlgorithm.CieLab;
                    break;
                case "esrilablchalgorithm":
                    algorithm = RampAlgorithm.LabLCh;
                    break;
                default:
                    error = InlineColorRampResolution.Unsupported(
                        $"Colorramp algorithm '{algorithmName}' is not supported. Supported algorithms: " +
                        "esriHSVAlgorithm, esriCIELabAlgorithm, esriLabLChAlgorithm.");
                    return false;
            }
        }

        segment = new Segment(from, to, algorithm);
        return true;
    }

    private static void AppendSegmentSamples(
        List<(double Fraction, ColorRgba Color)> samples,
        Segment segment,
        double gStart,
        double gEnd,
        bool includeStart)
    {
        for (var i = 0; i < SamplesPerSegment; i++)
        {
            if (i == 0 && !includeStart)
            {
                continue;
            }

            var t = (double)i / (SamplesPerSegment - 1);
            var color = Interpolate(segment, t);
            var fraction = gStart + ((gEnd - gStart) * t);
            samples.Add((fraction, color));
        }
    }

    private static RasterColormap BuildColormap(List<(double Fraction, ColorRgba Color)> samples)
    {
        var span = RangeMax - RangeMin;
        var entries = new List<RasterColormapEntry>(samples.Count);
        foreach (var (fraction, color) in samples)
        {
            var value = RangeMin + (fraction * span);
            entries.Add(new RasterColormapEntry(
                value,
                ToChannel(color.R),
                ToChannel(color.G),
                ToChannel(color.B),
                ToChannel(color.A)));
        }

        return new RasterColormap { Entries = entries };
    }

    private static ColorRgba Interpolate(Segment segment, double t)
    {
        var alpha = Lerp(segment.From.A, segment.To.A, t);
        return segment.Algorithm switch
        {
            RampAlgorithm.Hsv => InterpolateHsv(segment.From, segment.To, t) with { A = alpha },
            RampAlgorithm.LabLCh => InterpolateLabLCh(segment.From, segment.To, t) with { A = alpha },
            _ => InterpolateCieLab(segment.From, segment.To, t) with { A = alpha },
        };
    }

    // ----- Colour-space interpolation ---------------------------------------

    private static ColorRgba InterpolateHsv(ColorRgba from, ColorRgba to, double t)
    {
        var (h0, s0, v0) = RgbToHsv(from);
        var (h1, s1, v1) = RgbToHsv(to);
        var h = LerpAngle(h0, h1, t);
        var s = Lerp(s0, s1, t);
        var v = Lerp(v0, v1, t);
        return HsvToRgb(h, s, v);
    }

    private static ColorRgba InterpolateCieLab(ColorRgba from, ColorRgba to, double t)
    {
        var (l0, a0, b0) = RgbToLab(from);
        var (l1, a1, b1) = RgbToLab(to);
        return LabToRgb(Lerp(l0, l1, t), Lerp(a0, a1, t), Lerp(b0, b1, t));
    }

    private static ColorRgba InterpolateLabLCh(ColorRgba from, ColorRgba to, double t)
    {
        var (l0, a0, b0) = RgbToLab(from);
        var (l1, a1, b1) = RgbToLab(to);
        var c0 = Math.Sqrt((a0 * a0) + (b0 * b0));
        var c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        var h0 = Math.Atan2(b0, a0) * (180.0 / Math.PI);
        var h1 = Math.Atan2(b1, a1) * (180.0 / Math.PI);

        var l = Lerp(l0, l1, t);
        var c = Lerp(c0, c1, t);
        var h = LerpAngle(NormalizeAngle(h0), NormalizeAngle(h1), t) * (Math.PI / 180.0);
        return LabToRgb(l, c * Math.Cos(h), c * Math.Sin(h));
    }

    // ----- Colour-space conversions -----------------------------------------

    private static (double H, double S, double V) RgbToHsv(ColorRgba color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                h = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                h = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                h = 60 * (((r - g) / delta) + 4);
            }
        }

        h = NormalizeAngle(h);
        var s = max <= 0 ? 0 : delta / max;
        return (h, s, max);
    }

    private static ColorRgba HsvToRgb(double h, double s, double v)
    {
        h = NormalizeAngle(h);
        var c = v * s;
        var x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
        var m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return new ColorRgba((r + m) * 255.0, (g + m) * 255.0, (b + m) * 255.0, 255);
    }

    // sRGB (D65) reference white.
    private const double Xn = 0.95047;
    private const double Yn = 1.0;
    private const double Zn = 1.08883;
    private const double LabEpsilon = 0.008856; // (6/29)^3
    private const double LabKappa = 903.3;      // (29/3)^3

    private static (double L, double A, double B) RgbToLab(ColorRgba color)
    {
        var r = SrgbToLinear(color.R / 255.0);
        var g = SrgbToLinear(color.G / 255.0);
        var b = SrgbToLinear(color.B / 255.0);

        var x = ((0.4124564 * r) + (0.3575761 * g) + (0.1804375 * b)) / Xn;
        var y = ((0.2126729 * r) + (0.7151522 * g) + (0.0721750 * b)) / Yn;
        var z = ((0.0193339 * r) + (0.1191920 * g) + (0.9503041 * b)) / Zn;

        var fx = LabF(x);
        var fy = LabF(y);
        var fz = LabF(z);

        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static ColorRgba LabToRgb(double l, double a, double bStar)
    {
        var fy = (l + 16) / 116.0;
        var fx = fy + (a / 500.0);
        var fz = fy - (bStar / 200.0);

        var xr = LabFInverse(fx);
        var yr = l > (LabKappa * LabEpsilon) ? Math.Pow(fy, 3) : l / LabKappa;
        var zr = LabFInverse(fz);

        var x = xr * Xn;
        var y = yr * Yn;
        var z = zr * Zn;

        var r = (3.2404542 * x) - (1.5371385 * y) - (0.4985314 * z);
        var g = (-0.9692660 * x) + (1.8760108 * y) + (0.0415560 * z);
        var b = (0.0556434 * x) - (0.2040259 * y) + (1.0572252 * z);

        return new ColorRgba(
            LinearToSrgb(r) * 255.0,
            LinearToSrgb(g) * 255.0,
            LinearToSrgb(b) * 255.0,
            255);
    }

    private static double LabF(double t)
        => t > LabEpsilon ? Math.Cbrt(t) : (((LabKappa * t) + 16) / 116.0);

    private static double LabFInverse(double f)
    {
        var cubed = Math.Pow(f, 3);
        return cubed > LabEpsilon ? cubed : (((116 * f) - 16) / LabKappa);
    }

    private static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? 12.92 * c : (1.055 * Math.Pow(c, 1.0 / 2.4)) - 0.055;
    }

    // ----- Small helpers ----------------------------------------------------

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    // Interpolate a hue angle along the shortest arc so, e.g., 350° -> 10° crosses 0° rather than
    // sweeping the long way round the wheel.
    private static double LerpAngle(double a, double b, double t)
    {
        var delta = ((b - a + 540) % 360) - 180;
        return NormalizeAngle(a + (delta * t));
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        return angle < 0 ? angle + 360 : angle;
    }

    private static byte ToChannel(double value)
        => (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        foreach (var candidate in new[] { property, ToLowerFirst(property), property.ToLowerInvariant() })
        {
            if (element.TryGetProperty(candidate, out var child) && child.ValueKind == JsonValueKind.String)
            {
                value = child.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        return false;
    }

    private static bool TryParseColor(JsonElement element, string property, out ColorRgba color)
    {
        color = default;
        JsonElement array = default;
        var found = false;
        foreach (var candidate in new[] { property, ToLowerFirst(property), property.ToLowerInvariant() })
        {
            if (element.TryGetProperty(candidate, out array))
            {
                found = true;
                break;
            }
        }

        if (!found || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < 3)
        {
            return false;
        }

        if (!array[0].TryGetDouble(out var r) ||
            !array[1].TryGetDouble(out var g) ||
            !array[2].TryGetDouble(out var b))
        {
            return false;
        }

        var a = 255.0;
        if (array.GetArrayLength() >= 4 && array[3].TryGetDouble(out var parsedAlpha))
        {
            a = parsedAlpha;
        }

        color = new ColorRgba(
            Math.Clamp(r, 0, 255),
            Math.Clamp(g, 0, 255),
            Math.Clamp(b, 0, 255),
            Math.Clamp(a, 0, 255));
        return true;
    }

    private static string ToLowerFirst(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private enum RampAlgorithm
    {
        Hsv,
        CieLab,
        LabLCh,
    }

    private readonly record struct ColorRgba(double R, double G, double B, double A);

    private readonly record struct Segment(ColorRgba From, ColorRgba To, RampAlgorithm Algorithm);
}
