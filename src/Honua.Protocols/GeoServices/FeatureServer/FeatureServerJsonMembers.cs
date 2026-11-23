// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Protocols.GeoServices.FeatureServer;

// System.Text.Json throws InvalidOperationException when a value is read as the wrong kind, so
// caller-supplied JSON parameters are read through these kind-checked accessors and a mismatch
// is reported as invalid input rather than escaping as a server error.
internal static class FeatureServerJsonMembers
{
    public static string? ReadString(JsonElement parent, string member)
        => parent.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double? ReadNumber(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    public static double? ReadNumber(JsonElement parent, string member)
        => parent.TryGetProperty(member, out var value) ? ReadNumber(value) : null;

    public static int? ReadInt32(JsonElement parent, string member)
        => parent.TryGetProperty(member, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;

    public static bool HasMemberOfOtherKind(JsonElement parent, string member, JsonValueKind expected)
        => parent.TryGetProperty(member, out var value) &&
           value.ValueKind != JsonValueKind.Null &&
           value.ValueKind != expected;
}
