// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Parsed MapLibre expression value.
/// </summary>
internal readonly struct MapLibreExpression
{
    public MapLibreExpressionKind Kind { get; }

    public string? StringValue { get; }

    public double NumberValue { get; }

    public bool BoolValue { get; }

    public MapLibreExpression[]? Items { get; }

    public static MapLibreExpression Null => new(MapLibreExpressionKind.Null);

    public MapLibreExpression(string value)
        : this(MapLibreExpressionKind.String, stringValue: value)
    {
    }

    public MapLibreExpression(double value)
        : this(MapLibreExpressionKind.Number, numberValue: value)
    {
    }

    public MapLibreExpression(bool value)
        : this(MapLibreExpressionKind.Boolean, boolValue: value)
    {
    }

    public MapLibreExpression(MapLibreExpression[] items)
        : this(MapLibreExpressionKind.Array, items: items)
    {
    }

    private MapLibreExpression(
        MapLibreExpressionKind kind,
        string? stringValue = null,
        double numberValue = 0,
        bool boolValue = false,
        MapLibreExpression[]? items = null)
    {
        Kind = kind;
        StringValue = stringValue;
        NumberValue = numberValue;
        BoolValue = boolValue;
        Items = items;
    }
}

/// <summary>
/// Expression value kind.
/// </summary>
internal enum MapLibreExpressionKind
{
    Null = 0,
    String = 1,
    Number = 2,
    Boolean = 3,
    Array = 4
}
