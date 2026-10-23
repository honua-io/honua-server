// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Scene.Assets;

/// <summary>Double-precision transforms preserve placement of geocentric scene assets.</summary>
internal static class SceneAffineTransform
{
    public static readonly double[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    public static readonly double[] YUpToZUp = [1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1];

    public static double[] Multiply(double[] left, double[] right)
    {
        var result = new double[16];
        for (var column = 0; column < 4; column++)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var k = 0; k < 4; k++)
                {
                    result[column * 4 + row] += left[k * 4 + row] * right[column * 4 + k];
                }
            }
        }

        return result;
    }

    public static SceneCartesian Apply(double[] matrix, SceneCartesian point) => new(
        matrix[0] * point.X + matrix[4] * point.Y + matrix[8] * point.Z + matrix[12],
        matrix[1] * point.X + matrix[5] * point.Y + matrix[9] * point.Z + matrix[13],
        matrix[2] * point.X + matrix[6] * point.Y + matrix[10] * point.Z + matrix[14]);

    public static SceneCartesian ApplyNormal(double[] matrix, SceneCartesian normal)
    {
        var a = new SceneCartesian(matrix[0], matrix[1], matrix[2]);
        var b = new SceneCartesian(matrix[4], matrix[5], matrix[6]);
        var c = new SceneCartesian(matrix[8], matrix[9], matrix[10]);
        var cross = SceneCartesian.Cross(b, c);
        var determinant = a.X * cross.X + a.Y * cross.Y + a.Z * cross.Z;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-20)
        {
            throw new InvalidDataException("Singular mesh transform.");
        }

        var ca = SceneCartesian.Cross(c, a);
        var ab = SceneCartesian.Cross(a, b);
        var result = new SceneCartesian(cross.X * normal.X + ca.X * normal.Y + ab.X * normal.Z,
            cross.Y * normal.X + ca.Y * normal.Y + ab.Y * normal.Z,
            cross.Z * normal.X + ca.Z * normal.Y + ab.Z * normal.Z) / determinant;
        if (!result.IsFinite || result.Length < 1e-12)
        {
            throw new InvalidDataException("Invalid mesh normal.");
        }

        return result / result.Length;
    }

    public static double[] Read(JsonElement node, string matrixProperty)
    {
        if (node.TryGetProperty(matrixProperty, out var encoded))
        {
            if (encoded.ValueKind != JsonValueKind.Array || encoded.GetArrayLength() != 16)
            {
                throw new InvalidDataException("Invalid scene transform.");
            }

            var matrix = encoded.EnumerateArray().Select(value => value.GetDouble()).ToArray();
            if (matrix.Any(value => !double.IsFinite(value)) || matrix[3] != 0 || matrix[7] != 0 || matrix[11] != 0 || matrix[15] != 1)
            {
                throw new InvalidDataException("Scene transform must be finite and affine.");
            }

            return matrix;
        }

        var translation = ReadVector(node, "translation", [0, 0, 0]);
        var scale = ReadVector(node, "scale", [1, 1, 1]);
        var rotation = ReadVector(node, "rotation", [0, 0, 0, 1]);
        var (x, y, z, w) = (rotation[0], rotation[1], rotation[2], rotation[3]);
        if (Math.Abs(x * x + y * y + z * z + w * w - 1) > 0.00001)
        {
            throw new InvalidDataException("Invalid rotation quaternion.");
        }

        return [
            (1 - 2 * y * y - 2 * z * z) * scale[0], (2 * x * y + 2 * w * z) * scale[0], (2 * x * z - 2 * w * y) * scale[0], 0,
            (2 * x * y - 2 * w * z) * scale[1], (1 - 2 * x * x - 2 * z * z) * scale[1], (2 * y * z + 2 * w * x) * scale[1], 0,
            (2 * x * z + 2 * w * y) * scale[2], (2 * y * z - 2 * w * x) * scale[2], (1 - 2 * x * x - 2 * y * y) * scale[2], 0,
            translation[0], translation[1], translation[2], 1];
    }

    private static double[] ReadVector(JsonElement element, string name, double[] fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != fallback.Length)
        {
            throw new InvalidDataException("Invalid transform vector.");
        }

        var result = value.EnumerateArray().Select(component => component.GetDouble()).ToArray();
        if (result.Any(component => !double.IsFinite(component)))
        {
            throw new InvalidDataException("Non-finite transform vector.");
        }

        return result;
    }
}

/// <summary>A point or direction in the common ECEF Cartesian reference frame.</summary>
internal readonly record struct SceneCartesian(double X, double Y, double Z)
{
    public static SceneCartesian operator +(SceneCartesian a, SceneCartesian b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static SceneCartesian operator -(SceneCartesian a, SceneCartesian b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static SceneCartesian operator /(SceneCartesian a, double b) => new(a.X / b, a.Y / b, a.Z / b);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public static SceneCartesian Cross(SceneCartesian a, SceneCartesian b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
