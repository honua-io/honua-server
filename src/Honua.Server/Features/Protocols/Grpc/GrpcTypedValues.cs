// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.Grpc;

// Lossless geospatial-grpc 1.2 attribute values: calendar dates, local times, offset
// timestamps and exact decimals. Lexically invalid input throws ArgumentException, which
// the edit path reports as INVALID_ARGUMENT before any write.
internal static class GrpcTypedValues
{
    private const string DateFormat = "yyyy'-'MM'-'dd";
    private const string TimeFormat = "HH':'mm':'ss.FFFFFFF";

    public static DateOnly ParseDate(string value) => ExactAttributeValues.ParseDate(value);
    public static TimeOnly ParseTime(string value) => ExactAttributeValues.ParseTime(value);
    public static DateTimeOffset ParseTimestampOffset(string value)
        => PreserveRangeFailure(() => ExactAttributeValues.ParseTimestampOffset(value));
    public static decimal ParseDecimal(string value)
        => PreserveRangeFailure(() => ExactAttributeValues.ParseDecimal(value));

    private static T PreserveRangeFailure<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (ExactValueRangeException exception)
        {
            throw new UnstorableValueException(exception.Message);
        }
    }

    // Legacy field types keep their existing wire representation.
    public static string ToInvariantText(object value) => value switch
    {
        DateOnly date => FormatDate(date),
        TimeOnly time => FormatTime(time),
        Guid guid => guid.ToString("D"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    // Without trailing fractional zeros.
    public static string FormatTime(TimeOnly time) => time.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static GrpcExactValue ExactDecimal(string value)
    {
        _ = ParseDecimal(value);
        return new(MetadataV2FieldType.Decimal, value);
    }

    public static GrpcExactValue ExactTimestampOffset(string value)
    {
        _ = ParseTimestampOffset(value);
        return new(MetadataV2FieldType.TimestampOffset, value);
    }

    // Exact values use JSON strings in canonical storage; schema types supply their meaning.
    // The wire discriminator must agree with the declared type before entering the writer.
    public static FeatureEditBatch EnsureStorable(MetadataV2Object resource, FeatureEditBatch batch)
        => batch with
        {
            Creates = NormalizeFeatures(resource, batch.Creates),
            Updates = NormalizeFeatures(resource, batch.Updates),
            Operations = batch.Operations.IsDefault ? [] : batch.Operations.Select(operation => operation with
            {
                Feature = operation.Feature is { } feature ? NormalizeFeature(resource, feature) : null
            }).ToImmutableArray()
        };

    private static ImmutableArray<Feature> NormalizeFeatures(MetadataV2Object resource, ImmutableArray<Feature> features)
        => features.IsDefault ? [] : features.Select(feature => NormalizeFeature(resource, feature)).ToImmutableArray();

    private static Feature NormalizeFeature(MetadataV2Object resource, Feature feature)
    {
        var attributes = feature.Attributes.ToBuilder();
        foreach (var (name, value) in feature.Attributes)
        {
            var field = resource.SchemaFields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));
            if (value is GrpcExactValue exact)
            {
                if (field?.Type != exact.Type)
                {
                    throw CannotPreserve(name);
                }
                attributes[name] = exact.Text;
            }
            else if (value is decimal or DateTimeOffset)
            {
                throw CannotPreserve(name);
            }
            else if (value is not null && field?.Type is MetadataV2FieldType.Decimal or MetadataV2FieldType.TimestampOffset)
            {
                // Plain strings, numbers and legacy datetime values cannot substitute for an exact wire case.
                throw CannotPreserve(name);
            }
        }
        return feature with { Attributes = attributes.ToImmutable() };
    }

    private static RpcException CannotPreserve(string name)
        => new(new Status(StatusCode.FailedPrecondition, $"Field '{name}' requires a matching exact value and storage type."));
}

// A lexically valid value the server cannot hold without loss: FAILED_PRECONDITION, not INVALID_ARGUMENT.
internal sealed class UnstorableValueException(string message) : Exception(message);

internal readonly record struct GrpcExactValue(MetadataV2FieldType Type, string Text);
