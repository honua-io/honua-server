// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Honua.CustomCode.Harness;

// Container overrides are readable by anyone allowed to describe jobs in the account, so the
// provider passes only a single-use code. The server spends it on the first exchange; a copy read
// later is worthless, and if someone else redeemed it first this job fails closed.
internal static class CapabilityRedemption
{
    internal const string CodeHeader = "X-Honua-Redemption-Code";

    private const int MaximumCapabilityBytes = 256;
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(2);
    private static readonly Regex HexCode = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex JobIdentity = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);

    internal static string Redeem(RedemptionTarget target, string code, HttpMessageHandler? handler = null)
    {
        var uri = BuildUri(target, code);
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var clock = Stopwatch.StartNew();
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent([]) };
            request.Headers.Add(CodeHeader, code);
            try
            {
                using var response = client.Send(request);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return ReadCapability(response);
                }

                if (!IsRetryable(response.StatusCode) || clock.Elapsed >= RetryWindow)
                {
                    throw new InvalidOperationException($"Job capability redemption refused (HTTP {(int)response.StatusCode}).");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                if (clock.Elapsed >= RetryWindow)
                {
                    throw new InvalidOperationException("Job capability redemption did not complete before its retry deadline.");
                }
            }

            Thread.Sleep(RetryPause);
        }
    }

    private static Uri BuildUri(RedemptionTarget target, string code)
    {
        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var origin) ||
            (origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback)) ||
            !JobIdentity.IsMatch(target.JobId ?? string.Empty) ||
            !int.TryParse(target.Attempt, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var attempt) ||
            attempt <= 0 || !HexCode.IsMatch(code))
        {
            throw new ArgumentException("Job capability redemption requires HTTPS, server-assigned job/attempt identity and a well-formed code.");
        }

        return new Uri($"{target.BaseUrl.TrimEnd('/')}/api/geoprocessing/jobs/{target.JobId}/attempts/{attempt}/capability");
    }

    private static bool IsRetryable(HttpStatusCode status)
        => status is HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static string ReadCapability(HttpResponseMessage response)
    {
        using var stream = response.Content.ReadAsStream();
        var buffer = new byte[MaximumCapabilityBytes + 1];
        var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var capability = System.Text.Encoding.ASCII.GetString(buffer, 0, length).Trim();
        return HexCode.IsMatch(capability)
            ? capability
            : throw new InvalidOperationException("Job capability redemption returned a malformed capability.");
    }
}

internal sealed record RedemptionTarget(string BaseUrl, string? JobId, string? Attempt);
