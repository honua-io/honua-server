// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.CustomCode.Sdk;
using System.Net;
using System.Text.RegularExpressions;

namespace Honua.CustomCode.Harness;

/// <summary>The result of uploading one artifact.</summary>
/// <param name="Name">The artifact name.</param>
/// <param name="Uri">The authenticated artifact URI.</param>
/// <param name="SizeBytes">The uploaded size in bytes.</param>
public sealed record UploadResult(string Name, string Uri, long SizeBytes);

/// <summary>
/// Uploads through the production job-scoped HTTP broker. The legacy S3 constructor
/// accepts an explicit callback for offline tests only; no cloud credential discovery occurs.
/// </summary>
public sealed class ArtifactUploader
{
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly Action<string, string, string> _put;
    private readonly Func<Artifact, string>? _brokerPut;

    /// <summary>Creates an uploader for an <c>s3://bucket/prefix</c> destination.</summary>
    /// <param name="outputPrefix">The job's <c>s3://bucket/prefix</c> output prefix.</param>
    /// <param name="put">An injectable <c>(bucket, key, localPath)</c> put callback required for legacy test transport.</param>
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
        _put = put ?? throw new ArgumentException("Legacy S3 transport requires an explicit callback; production uses the job artifact broker.", nameof(put));
    }

    /// <summary>Upload each artifact under <c>s3://bucket/prefix/&lt;name&gt;</c>.</summary>
    /// <param name="artifacts">The artifacts to upload.</param>
    /// <returns>The per-artifact upload results.</returns>
    public IReadOnlyList<UploadResult> Upload(IEnumerable<Artifact> artifacts)
    {
        var results = new List<UploadResult>();
        foreach (var artifact in artifacts)
        {
            if (_brokerPut is not null)
            {
                results.Add(new UploadResult(artifact.Name, _brokerPut(artifact), artifact.SizeBytes));
                continue;
            }
            var key = KeyFor(artifact.Name);
            _put(_bucket, key, artifact.Path);
            results.Add(new UploadResult(artifact.Name, $"s3://{_bucket}/{key}", artifact.SizeBytes));
        }

        return results;
    }

    private string KeyFor(string name)
        => string.IsNullOrEmpty(_prefix) ? name : $"{_prefix.TrimEnd('/')}/{name}";

    private ArtifactUploader(Func<Artifact, string> brokerPut)
    {
        _bucket = _prefix = string.Empty;
        _put = (_, _, _) => throw new InvalidOperationException("The job artifact broker is required.");
        _brokerPut = brokerPut;
    }

    /// <summary>Creates the production credential-free job/attempt-bound HTTP transport.</summary>
    /// <param name="baseUrl">Serving API origin.</param>
    /// <param name="token">Scoped job capability.</param>
    /// <param name="jobId">Server-assigned job identity.</param>
    /// <param name="attempt">Server-assigned attempt.</param>
    public static ArtifactUploader CreateBroker(string baseUrl, string token, string? jobId, string? attempt)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) ||
            (origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback)) ||
            !Regex.IsMatch(jobId ?? string.Empty, "^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant) ||
            !int.TryParse(attempt, out var attemptNumber) || attemptNumber <= 0)
        {
            throw new ArgumentException("Artifact broker requires HTTPS and server-assigned job/attempt identity.");
        }
        var prefix = baseUrl.TrimEnd('/') + "/api/geoprocessing/jobs/" + jobId + "/custom-artifacts/";
        return new ArtifactUploader(artifact =>
        {
            if (!Regex.IsMatch(artifact.Name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
            {
                throw new ArgumentException("Artifact name must be a single safe filename.");
            }
            var size = new FileInfo(artifact.Path).Length;
            if (size > 64 * 1024 * 1024 || size != artifact.SizeBytes)
            {
                throw new ArgumentException("Artifact size exceeds the broker limit or changed after registration.");
            }
            var uri = prefix + artifact.Name + "?attempt=" + attempt;
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var retryClock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                using var stream = File.OpenRead(artifact.Path);
                using var request = new HttpRequestMessage(HttpMethod.Put, uri);
                request.Headers.Add("X-Api-Key", token);
                request.Content = new StreamContent(stream);
                request.Content.Headers.ContentLength = size;
                request.Content.Headers.ContentType = new("application/octet-stream");
                try
                {
                    using var response = client.Send(request);
                    if (response.IsSuccessStatusCode)
                    {
                        return uri;
                    }
                    var retryable = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests ||
                        (int)response.StatusCode >= 500;
                    if (!retryable || retryClock.Elapsed >= TimeSpan.FromMinutes(2))
                    {
                        throw new InvalidOperationException($"Artifact broker refused upload (HTTP {(int)response.StatusCode}).");
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    if (retryClock.Elapsed >= TimeSpan.FromMinutes(2))
                    {
                        throw new InvalidOperationException("Artifact broker upload did not complete before its retry deadline.");
                    }
                }
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        });
    }
}
