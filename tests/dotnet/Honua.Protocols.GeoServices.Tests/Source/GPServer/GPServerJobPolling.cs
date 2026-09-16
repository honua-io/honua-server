// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Shared job-status polling for GPServer end-to-end tests that drive a job to
/// <c>esriJobSucceeded</c> through the real durable worker (honua-server#4965).
/// The worker's claim loop (<c>Honua.Jobs.Features.ControlPlane.JobExecutionService</c>)
/// only retries every <c>PollInterval</c> (5s in production) when the queue is
/// momentarily empty, and a shard running dozens of concurrent <c>WebAppFixture</c>
/// hosts can stretch that further under host contention. A short, fixed poll deadline
/// therefore reads as a hung job when the real cause is scheduler pressure, not a
/// regression — three different timing reds in twelve hours on trunk. The budget here
/// is a generous shard-level allowance (12x the production poll interval) rather than
/// a tight timing derivation, and a timeout reports the job's last observed status and
/// message list so a genuine hang stays diagnosable.
/// </summary>
internal static class GPServerJobPolling
{
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(60);

    internal static async Task<JsonDocument> PollUntilSucceededAsync(
        HttpClient client,
        string statusUrl,
        string jobId,
        string becauseNotFailed,
        TimeSpan? budget = null)
    {
        var effectiveBudget = budget ?? DefaultBudget;
        var deadline = DateTimeOffset.UtcNow.Add(effectiveBudget);
        string? lastStatus = null;
        string? lastBody = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync(statusUrl);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("jobStatus").GetString();
            lastStatus = status;
            lastBody = body;
            if (status == "esriJobSucceeded")
            {
                return JsonDocument.Parse(body);
            }

            status.Should().NotBe("esriJobFailed", becauseNotFailed);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"Timed out waiting for GPServer job '{jobId}' to succeed after {effectiveBudget}. " +
            $"Last observed jobStatus: '{lastStatus}'. Last response body: {lastBody}");
    }

    /// <summary>
    /// Polls until the job reaches any terminal status (succeeded, failed, or
    /// cancelled), or throws with the last observed status/body if the shard-level
    /// budget is exhausted first — unlike <see cref="PollUntilSucceededAsync"/>, a
    /// non-succeeded terminal status is not itself a failure here; the caller asserts
    /// on the returned status.
    /// </summary>
    internal static async Task<string?> PollUntilTerminalAsync(
        HttpClient client,
        string statusUrl,
        string jobId,
        TimeSpan? budget = null)
    {
        var effectiveBudget = budget ?? DefaultBudget;
        var deadline = DateTimeOffset.UtcNow.Add(effectiveBudget);
        string? lastStatus = null;
        string? lastBody = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync(statusUrl);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("jobStatus").GetString();
            lastStatus = status;
            lastBody = body;
            if (status is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled")
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            $"Timed out waiting for GPServer job '{jobId}' to reach a terminal status after {effectiveBudget}. " +
            $"Last observed jobStatus: '{lastStatus}'. Last response body: {lastBody}");
    }
}
