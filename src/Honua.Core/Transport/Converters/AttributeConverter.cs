// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using Geospatial.V1;

namespace Honua.Core.Transport.Converters;

/// <summary>
/// Converter for feature attributes between domain models and gRPC messages.
/// Handles type-safe conversion of various attribute value types.
/// </summary>
public static class AttributeConverter
{
    /// <summary>
    /// Converts a domain attribute value to a gRPC AttributeValue message.
    /// </summary>
    /// <param name="value">The domain attribute value</param>
    /// <returns>gRPC attribute value message</returns>
    public static AttributeValue ToGrpc(object? value)
    {
        var grpcValue = new AttributeValue();

        if (value == null)
        {
            grpcValue.NullValue = NullValue.NullValue;
            return grpcValue;
        }

        switch (value)
        {
            case string stringValue:
                grpcValue.StringValue = stringValue;
                break;

            case int intValue:
                grpcValue.Int32Value = intValue;
                break;

            case long longValue:
                grpcValue.Int64Value = longValue;
                break;

            case float floatValue:
                grpcValue.FloatValue = floatValue;
                break;

            case double doubleValue:
                grpcValue.DoubleValue = doubleValue;
                break;

            case bool boolValue:
                grpcValue.BoolValue = boolValue;
                break;

            case DateTime dateTimeValue:
                grpcValue.DatetimeValue = new DateTimeOffset(dateTimeValue).ToUnixTimeMilliseconds();
                break;

            case DateTimeOffset dateTimeOffsetValue:
                grpcValue.DatetimeValue = dateTimeOffsetValue.ToUnixTimeMilliseconds();
                break;

            case byte[] bytesValue:
                grpcValue.BytesValue = Google.Protobuf.ByteString.CopyFrom(bytesValue);
                break;

            default:
                // For other types, convert to string as fallback
                grpcValue.StringValue = value.ToString() ?? string.Empty;
                break;
        }

        return grpcValue;
    }

    /// <summary>
    /// Converts a gRPC AttributeValue message to a domain attribute value.
    /// </summary>
    /// <param name="grpcValue">The gRPC attribute value message</param>
    /// <returns>Domain attribute value</returns>
    public static object? FromGrpc(AttributeValue grpcValue)
    {
        return grpcValue.ValueCase switch
        {
            AttributeValue.ValueOneofCase.StringValue => grpcValue.StringValue,
            AttributeValue.ValueOneofCase.Int32Value => grpcValue.Int32Value,
            AttributeValue.ValueOneofCase.Int64Value => grpcValue.Int64Value,
            AttributeValue.ValueOneofCase.FloatValue => grpcValue.FloatValue,
            AttributeValue.ValueOneofCase.DoubleValue => grpcValue.DoubleValue,
            AttributeValue.ValueOneofCase.BoolValue => grpcValue.BoolValue,
            AttributeValue.ValueOneofCase.DatetimeValue => DateTimeOffset.FromUnixTimeMilliseconds(grpcValue.DatetimeValue).DateTime,
            AttributeValue.ValueOneofCase.BytesValue => grpcValue.BytesValue.ToByteArray(),
            AttributeValue.ValueOneofCase.NullValue => null,
            AttributeValue.ValueOneofCase.None => null,
            _ => null
        };
    }

    /// <summary>
    /// Converts a gRPC attribute dictionary to a domain attribute dictionary.
    /// </summary>
    /// <param name="grpcAttributes">The gRPC attributes map</param>
    /// <returns>Domain attributes dictionary</returns>
    public static ImmutableDictionary<string, object?> FromGrpc(Google.Protobuf.Collections.MapField<string, AttributeValue> grpcAttributes)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>();

        foreach (var kvp in grpcAttributes)
        {
            builder[kvp.Key] = FromGrpc(kvp.Value);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Converts a domain attribute dictionary to a gRPC attribute dictionary.
    /// </summary>
    /// <param name="domainAttributes">The domain attributes dictionary</param>
    /// <param name="grpcAttributes">The target gRPC attributes map to populate</param>
    public static void ToGrpc(ImmutableDictionary<string, object?> domainAttributes, Google.Protobuf.Collections.MapField<string, AttributeValue> grpcAttributes)
    {
        foreach (var kvp in domainAttributes)
        {
            grpcAttributes[kvp.Key] = ToGrpc(kvp.Value);
        }
    }
}