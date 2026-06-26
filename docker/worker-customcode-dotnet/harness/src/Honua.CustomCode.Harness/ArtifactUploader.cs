// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.CustomCode.Sdk;

namespace Honua.CustomCode.Harness;

/// <summary>The result of uploading one artifact.</summary>
/// <param name="Name">The artifact name.</param>
/// <param name="Uri">The destination S3 URI.</param>
/// <param name="SizeBytes">The uploaded size in bytes.</param>
public sealed record UploadResult(string Name, string Uri, long SizeBytes);

/// <summary>
/// Uploads staged artifacts to the job's S3 <c>output_prefix</c>. This runs AFTER user
/// code returns and AFTER the credential scrub, so it uses an injected upload callback
/// (in production a dedicated upload credential the strip step does not remove). The
/// callback is injectable so the orchestrator stays testable offline. Mirrors the
/// Python harness's <c>upload.py</c>.
/// </summary>
public sealed class ArtifactUploader
{
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly Action<string, string, string> _put;

    /// <summary>Creates an uploader for an <c>s3://bucket/prefix</c> destination.</summary>
    /// <param name="outputPrefix">The job's <c>s3://bucket/prefix</c> output prefix.</param>
    /// <param name="put">An injectable <c>(bucket, key, localPath)</c> put callback; defaults to a real S3 put.</param>
    public ArtifactUploader(string outputPrefix, Action<string, string, string>? put = null)
    {
        if (!Uri.TryCreate(outputPrefix, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "s3", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException($"output_prefix must be s3://bucket/prefix, got '{outputPrefix}'.", nameof(outputPrefix));
        }

        _bucket = uri.Host;
        _prefix = uri.AbsolutePath.TrimStart('/');
        _put = put ?? DefaultS3Put;
    }

    /// <summary>Upload each artifact under <c>s3://bucket/prefix/&lt;name&gt;</c>.</summary>
    /// <param name="artifacts">The artifacts to upload.</param>
    /// <returns>The per-artifact upload results.</returns>
    public IReadOnlyList<UploadResult> Upload(IEnumerable<Artifact> artifacts)
    {
        var results = new List<UploadResult>();
        foreach (var artifact in artifacts)
        {
            var key = KeyFor(artifact.Name);
            _put(_bucket, key, artifact.Path);
            results.Add(new UploadResult(artifact.Name, $"s3://{_bucket}/{key}", artifact.SizeBytes));
        }

        return results;
    }

    private string KeyFor(string name)
        => string.IsNullOrEmpty(_prefix) ? name : $"{_prefix.TrimEnd('/')}/{name}";

    private static void DefaultS3Put(string bucket, string key, string path)
    {
        // The real S3 put is wired in the image where the AWS SDK + upload credential
        // are present. Kept out of the offline harness build so the host stays
        // dependency-light and fully testable with an injected fake.
        throw new NotSupportedException(
            "No S3 put callback was provided. The image wires the AWS SDK put at runtime.");
    }
}
