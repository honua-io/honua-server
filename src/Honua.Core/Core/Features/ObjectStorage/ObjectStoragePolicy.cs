// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.ObjectStorage;

// Validation for every client-supplied object storage value. The region and bucket end up in a host name,
// and keys end up in S3 requests, so each is checked against fixed rules rather than trusted.
public static partial class ObjectStoragePolicy
{
    public const int MaxKeyBytes = 1024;

    private static readonly string[] ForbiddenBucketPrefixes = ["xn--", "sthree-", "amzn-s3-demo-"];

    private static readonly string[] ForbiddenBucketSuffixes = ["-s3alias", "--ol-s3", ".mrap", "--x-s3", "--table-s3"];

    private static readonly HashSet<string> KnownRegions = new(StringComparer.Ordinal)
    {
        "us-east-1", "us-east-2", "us-west-1", "us-west-2",
        "af-south-1",
        "ap-east-1", "ap-east-2", "ap-south-1", "ap-south-2",
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3",
        "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4", "ap-southeast-5", "ap-southeast-6", "ap-southeast-7",
        "ca-central-1", "ca-west-1",
        "eu-central-1", "eu-central-2", "eu-west-1", "eu-west-2", "eu-west-3",
        "eu-south-1", "eu-south-2", "eu-north-1",
        "il-central-1", "me-south-1", "me-central-1", "mx-central-1", "sa-east-1",
    };

    public static bool IsKnownRegion(string? region) => region is not null && KnownRegions.Contains(region);

    public static bool IsValidBucketName(string? bucket)
    {
        if (bucket is null || bucket.Length is < 3 or > 63 || !BucketCharacters().IsMatch(bucket))
        {
            return false;
        }

        return !bucket.Contains("..", StringComparison.Ordinal)
            && !IpShaped().IsMatch(bucket)
            && !ForbiddenBucketPrefixes.Any(prefix => bucket.StartsWith(prefix, StringComparison.Ordinal))
            && !ForbiddenBucketSuffixes.Any(suffix => bucket.EndsWith(suffix, StringComparison.Ordinal));
    }

    // arn:aws:iam::<12 digits>:role/[path/]honua-connection-<name>
    public static bool IsValidRoleArn(string? roleArn) => roleArn is not null && RoleArnPattern().IsMatch(roleArn);

    // Accepts "" or a relative path and returns it with exactly one trailing '/'; null when refused.
    public static string? NormalizePrefix(string? prefix)
    {
        var candidate = prefix?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        var withSlash = candidate.EndsWith('/') ? candidate : candidate + "/";
        // The prefix becomes an IAM resource, where '*', '?' and '${' are wildcards or variables.
        return IsSafePath(withSlash[..^1]) && withSlash.IndexOfAny(['*', '?', '$']) < 0 ? withSlash : null;
    }

    // A key is admitted only when it is a safe relative path that lies under the connection prefix.
    public static bool IsKeyUnderPrefix(string connectionPrefix, string? key)
    {
        ArgumentNullException.ThrowIfNull(connectionPrefix);
        return key is not null
            && IsSafePath(key)
            && key.StartsWith(connectionPrefix, StringComparison.Ordinal)
            && key.Length > connectionPrefix.Length;
    }

    // A browse prefix may equal the connection prefix or name a folder below it; null when refused.
    public static string? ResolveBrowsePrefix(string connectionPrefix, string? requested)
    {
        ArgumentNullException.ThrowIfNull(connectionPrefix);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return connectionPrefix;
        }

        var normalized = NormalizePrefix(requested);
        return normalized is not null && normalized.StartsWith(connectionPrefix, StringComparison.Ordinal) ? normalized : null;
    }

    // 32 random bytes as lowercase hex; within the STS ExternalId character set.
    public static string NewExternalId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    // Built-ins have no stored row; the same configuration always yields the same id.
    public static Guid BuiltInId(string role, string bucket, string prefix)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"honua-builtin-object-storage\n{role}\n{bucket}\n{prefix}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static bool IsSafePath(string path)
    {
        if (path.Length == 0
            || Encoding.UTF8.GetByteCount(path) > MaxKeyBytes
            || path[0] == '/'
            || path.Any(char.IsControl)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal)
            || path.Contains("/vsi", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("vsi", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.TrimEnd('/').Split('/');
        return segments.All(static segment => segment.Length > 0 && segment != ".");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketCharacters();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IpShaped();

    [GeneratedRegex(@"\Aarn:aws:iam::\d{12}:role/(?:[A-Za-z0-9+=,.@_-]+/)*honua-connection-[A-Za-z0-9+=,.@_-]{1,47}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleArnPattern();
}
