// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Tests.Features.Admin;

internal static class ArtifactHttpAssertions
{
    internal static async Task AssertIgnoredRangePreservesBudgetAsync(
        HttpClient client, string url, ICloudFileStorage storage, string artifactId)
    {
        var metadata = (await storage.GetMetadataAsync(artifactId))!;
        var bytes = new byte[PMTilesProxyService.MaxDirectFullStreamBytes + 1];
        await using var content = new MemoryStream(bytes, writable: false);
        var upload = await storage.UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = metadata.FileName,
            ObjectKeyOverride = metadata.FileId,
            ContentType = metadata.ContentType,
            SizeBytes = bytes.LongLength,
            // Publisher inputs contain source metadata, not the storage-owned upload stamp.
            Metadata = metadata.Metadata.Remove("UploadId"),
            TimeToLive = null
        });
        upload.Success.Should().BeTrue(upload.ErrorMessage);

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Content.Headers.ContentLength.Should().Be(bytes.LongLength);
        using var stale = new HttpRequestMessage(HttpMethod.Get, url);
        stale.Headers.Range = new RangeHeaderValue(0, 6);
        stale.Headers.TryAddWithoutValidation("If-Range", "\"stale\"");
        using var refused = await client.SendAsync(stale);
        refused.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge,
            "ignoring Range must retain the full-transfer budget");
        refused.Content.Headers.ContentRange.Should().BeNull();

        using var matching = new HttpRequestMessage(HttpMethod.Get, url);
        matching.Headers.Range = new RangeHeaderValue(0, 6);
        matching.Headers.TryAddWithoutValidation("If-Range", head.Headers.ETag!.ToString());
        using var partial = await client.SendAsync(matching);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(bytes.Take(7));
    }

    internal static async Task AssertConditionalReadsAsync(HttpClient client, string url)
    {
        using var baseline = await client.GetAsync(url);
        baseline.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await baseline.Content.ReadAsByteArrayAsync();
        var etag = baseline.Headers.ETag!;
        etag.Should().NotBeNull();
        etag.IsWeak.Should().BeFalse("published fixtures carry an actual storage revision identity");
        var modified = baseline.Content.Headers.LastModified!.Value;

        foreach (var validator in new[] { etag.ToString(), "\"stale\"", "W/" + etag, modified.ToString("R"), modified.AddDays(-1).ToString("R") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 6);
            request.Headers.TryAddWithoutValidation("If-Range", validator);
            using var response = await client.SendAsync(request);
            if (validator == etag.ToString())
            {
                response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
                (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes.Take(7));
                response.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 0-6/{bytes.Length}");
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK, "an unproven/stale validator must ignore Range");
                (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
                response.Content.Headers.ContentRange.Should().BeNull();
            }
        }

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            foreach (var (header, value, expected) in new[]
            {
                ("If-None-Match", etag.ToString(), HttpStatusCode.NotModified),
                ("If-None-Match", "W/" + etag, HttpStatusCode.NotModified),
                ("If-None-Match", "*", HttpStatusCode.NotModified),
                ("If-Match", "\"stale\"", HttpStatusCode.PreconditionFailed),
                ("If-Match", "W/" + etag, HttpStatusCode.PreconditionFailed),
                ("If-Unmodified-Since", modified.AddDays(-1).ToString("R"), HttpStatusCode.PreconditionFailed),
                ("If-Modified-Since", modified.ToString("R"), HttpStatusCode.NotModified)
            })
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.TryAddWithoutValidation(header, value);
                request.Headers.Range = new RangeHeaderValue(0, 6);
                using var response = await client.SendAsync(request);
                response.StatusCode.Should().Be(expected, $"{method} {header} must run before Range");
                (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
                response.Content.Headers.ContentRange.Should().BeNull();
            }

            using var precedence = new HttpRequestMessage(method, url);
            precedence.Headers.IfMatch.Add(etag);
            precedence.Headers.IfUnmodifiedSince = modified.AddDays(-1);
            precedence.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale\""));
            precedence.Headers.IfModifiedSince = modified.AddDays(1);
            using var allowed = await client.SendAsync(precedence);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, "entity tags take precedence over corresponding dates");
            (await allowed.Content.ReadAsByteArrayAsync()).Should().Equal(method == HttpMethod.Head ? [] : bytes);
        }
    }
}
