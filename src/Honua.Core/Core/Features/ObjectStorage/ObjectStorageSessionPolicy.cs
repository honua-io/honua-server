// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;

namespace Honua.Core.Features.ObjectStorage;

// The inline policy passed with every AssumeRole. The effective session is the intersection of the
// customer role and this document, so a role broader than one bucket prefix is still read-only on it.
public static class ObjectStorageSessionPolicy
{
    public const int DurationSeconds = 900;

    public static string Build(string bucket, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(prefix);
        if (!ObjectStoragePolicy.IsValidBucketName(bucket)
            || !string.Equals(ObjectStoragePolicy.NormalizePrefix(prefix), prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The bucket or prefix is not a validated connection value.", nameof(bucket));
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("Version", "2012-10-17");
            writer.WriteStartArray("Statement");
            WriteStatement(writer, new Statement("ReadObjects", "s3:GetObject", $"arn:aws:s3:::{bucket}/{prefix}*"));
            WriteListStatement(writer, bucket, prefix);
            WriteStatement(writer, new Statement("BucketLocation", "s3:GetBucketLocation", $"arn:aws:s3:::{bucket}"));
            WriteStatement(writer, new Statement("BucketRequestPayment", "s3:GetBucketRequestPayment", $"arn:aws:s3:::{bucket}"));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteStatement(Utf8JsonWriter writer, Statement statement)
    {
        writer.WriteStartObject();
        writer.WriteString("Sid", statement.Sid);
        writer.WriteString("Effect", "Allow");
        writer.WriteString("Action", statement.Action);
        writer.WriteString("Resource", statement.Resource);
        writer.WriteEndObject();
    }

    private sealed record Statement(string Sid, string Action, string Resource);

    private static void WriteListStatement(Utf8JsonWriter writer, string bucket, string prefix)
    {
        writer.WriteStartObject();
        writer.WriteString("Sid", "ListPrefix");
        writer.WriteString("Effect", "Allow");
        writer.WriteString("Action", "s3:ListBucket");
        writer.WriteString("Resource", $"arn:aws:s3:::{bucket}");
        writer.WriteStartObject("Condition");
        writer.WriteStartObject("StringLike");
        writer.WriteStartArray("s3:prefix");
        writer.WriteStringValue(prefix + "*");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
