// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Shared.Models;

// Manifest of canonical CLR scalar and container types that can appear inside polymorphic
// feature attribute bags. Registering this type via [JsonSerializable(typeof(AotFeatureAttributeTypes))]
// instructs the System.Text.Json source generator to emit AOT-safe metadata for every listed property type.
public sealed class AotFeatureAttributeTypes
{
    public string? StringValue { get; }
    public bool BoolValue { get; }
    public byte ByteValue { get; }
    public sbyte SByteValue { get; }
    public short ShortValue { get; }
    public ushort UShortValue { get; }
    public int IntValue { get; }
    public uint UIntValue { get; }
    public long LongValue { get; }
    public ulong ULongValue { get; }
    public float FloatValue { get; }
    public double DoubleValue { get; }
    public decimal DecimalValue { get; }
    public DateTime DateTimeValue { get; }
    public DateTimeOffset DateTimeOffsetValue { get; }
    public DateOnly DateOnlyValue { get; }
    public TimeOnly TimeOnlyValue { get; }
    public Guid GuidValue { get; }
    public byte[]? ByteArrayValue { get; }
    public JsonElement JsonElementValue { get; }
    public object? ObjectValue { get; }
    public object[]? ObjectArrayValue { get; }
    public string[]? StringArrayValue { get; }
    public int[]? IntArrayValue { get; }
    public long[]? LongArrayValue { get; }
    public double[]? DoubleArrayValue { get; }
    public List<object?>? ObjectListValue { get; }
    public Dictionary<string, object?>? AttributeDictionaryValue { get; }
}
