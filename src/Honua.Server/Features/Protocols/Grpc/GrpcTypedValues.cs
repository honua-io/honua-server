// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>
/// Lossless geospatial-grpc 1.2 attribute values: calendar dates, local times, offset
/// timestamps and exact decimals (specification "AttributeValue"). Lexically invalid
/// input throws <see cref="ArgumentException"/>, which the edit path reports as
/// <c>INVALID_ARGUMENT</c> before any write.
/// </summary>
internal static partial class GrpcTypedValues
{
    private const string DateFormat = "yyyy'-'MM'-'dd";
    private const string TimeFormat = "HH':'mm':'ss.FFFFFFF";

    private static readonly string[] TimeFormats =
    [
        "HH':'mm':'ss",
        "HH':'mm':'ss'.'f", "HH':'mm':'ss'.'ff", "HH':'mm':'ss'.'fff", "HH':'mm':'ss'.'ffff",
        "HH':'mm':'ss'.'fffff", "HH':'mm':'ss'.'ffffff", "HH':'mm':'ss'.'fffffff",
    ];

    private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}(\.\d{1,7})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(
        @"^(?<local>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?)(?<zone>Z|(?<sign>[+-])(?<hours>\d{2}):(?<minutes>\d{2}))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimestampOffsetPattern();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();

    /// <summary>Parses a <c>date_value</c> (Gregorian <c>YYYY-MM-DD</c>, years 0001-9999).</summary>
    public static DateOnly ParseDate(string value)
    {
        if (!DatePattern().IsMatch(value)
            || !DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ArgumentException($"date_value '{value}' is not a calendar date in YYYY-MM-DD form.");
        }

        return date;
    }

    /// <summary>Parses a <c>time_value</c> (<c>HH:mm:ss</c> with 0-7 fractional digits, 00:00:00-23:59:59.9999999).</summary>
    public static TimeOnly ParseTime(string value)
    {
        // TimeOnly parsing accepts 24:00:00; the wire range ends at 23:59:59.9999999.
        if (!TimePattern().IsMatch(value)
            || value.StartsWith("24", StringComparison.Ordinal)
            || !TimeOnly.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new ArgumentException($"time_value '{value}' is not a local time in HH:mm:ss[.fffffff] form.");
        }

        return time;
    }

    /// <summary>Parses a <c>timestamp_offset_value</c> (RFC 3339 with a required offset within ±14:00).</summary>
    public static DateTimeOffset ParseTimestampOffset(string value)
    {
        var match = TimestampOffsetPattern().Match(value);
        if (!match.Success)
        {
            throw new ArgumentException($"timestamp_offset_value '{value}' is not an RFC 3339 timestamp with an offset.");
        }

        var offset = TimeSpan.Zero;
        if (match.Groups["sign"].Success)
        {
            var hours = int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
            offset = new TimeSpan(hours, minutes, 0);
            if (minutes > 59 || offset > MaxOffset)
            {
                throw new ArgumentException($"timestamp_offset_value '{value}' has an offset outside ±14:00.");
            }

            if (match.Groups["sign"].Value == "-")
            {
                offset = offset.Negate();
            }
        }

        var local = match.Groups["local"].Value;
        var date = ParseDate(local[..10]);
        var time = ParseTime(local[11..]);
        try
        {
            return new DateTimeOffset(date.ToDateTime(time, DateTimeKind.Unspecified), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException($"timestamp_offset_value '{value}' is outside the representable range.");
        }
    }

    /// <summary>
    /// Parses a <c>decimal_value</c> exactly. Values the server cannot hold without rounding
    /// (more than 28-29 significant digits or out of range) are refused, never rounded.
    /// </summary>
    public static decimal ParseDecimal(string value)
    {
        if (!DecimalPattern().IsMatch(value)
            || !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"decimal_value '{value}' is not an exact base-10 number.");
        }

        var exact = number.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(exact, value, StringComparison.Ordinal)
            && !string.Equals(exact, NormalizeNegativeZero(value), StringComparison.Ordinal))
        {
            throw new ArgumentException($"decimal_value '{value}' exceeds the supported decimal precision.");
        }

        return number;
    }

    /// <summary>Formats a calendar date as <c>date_value</c>.</summary>
    public static Proto.AttributeValue FromDate(DateOnly date)
        => new() { DateValue = date.ToString(DateFormat, CultureInfo.InvariantCulture) };

    /// <summary>Formats a local time as <c>time_value</c>, without trailing fractional zeros.</summary>
    public static Proto.AttributeValue FromTime(TimeOnly time)
        => new() { TimeValue = time.ToString(TimeFormat, CultureInfo.InvariantCulture) };

    /// <summary>
    /// Refuses, before any write, values whose target column cannot keep them: an offset
    /// timestamp (metadata has no offset-preserving field type; UTC instants use
    /// <c>datetime_value</c>) and a decimal that a double or integer column would round.
    /// </summary>
    public static void EnsureStorable(MetadataV2Resource resource, FeatureEditBatch batch)
    {
        var creates = batch.Creates.IsDefault ? ImmutableArray<Feature>.Empty : batch.Creates;
        var updates = batch.Updates.IsDefault ? ImmutableArray<Feature>.Empty : batch.Updates;
        foreach (var feature in creates.Concat(updates))
        {
            foreach (var (name, value) in feature.Attributes)
            {
                if (Unstorable(resource, name, value) is { } reason)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, reason));
                }
            }
        }
    }

    private static string? Unstorable(MetadataV2Resource resource, string name, object? value) => value switch
    {
        DateTimeOffset => $"Field '{name}' cannot preserve a timestamp offset; send datetime_value for a UTC instant.",
        decimal number when !FitsField(FieldType(resource, name), number)
            => $"Field '{name}' cannot hold decimal_value {number.ToString(CultureInfo.InvariantCulture)} without rounding.",
        _ => null,
    };

    private static MetadataV2FieldType? FieldType(MetadataV2Resource resource, string name)
        => resource.SchemaFields.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Type;

    private static bool FitsField(MetadataV2FieldType? type, decimal number) => type switch
    {
        MetadataV2FieldType.Double => RoundTrips(((double)number).ToString("R", CultureInfo.InvariantCulture), number),
        MetadataV2FieldType.Float => RoundTrips(((float)number).ToString("R", CultureInfo.InvariantCulture), number),
        MetadataV2FieldType.Integer => decimal.Truncate(number) == number && number is >= int.MinValue and <= int.MaxValue,
        MetadataV2FieldType.BigInteger => decimal.Truncate(number) == number && number is >= long.MinValue and <= long.MaxValue,
        _ => true,
    };

    // The binary value's shortest decimal text must denote the same number: 0.1 fits a double,
    // 0.12345678901234567890 does not.
    private static bool RoundTrips(string text, decimal number)
        => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var back) && back == number;

    // decimal keeps "-0" only as "0"; scale and trailing zeros otherwise round-trip.
    private static string NormalizeNegativeZero(string value)
        => value.StartsWith('-') && value.AsSpan(1).TrimStart('0').TrimStart('.').TrimStart('0').IsEmpty
            ? value[1..]
            : value;
}
