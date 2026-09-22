// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Waits for durable GPServer jobs driven through the real Redis-backed worker (#4965).
/// </summary>
/// <remarks>
/// The deadlines come from the runtime's own timing instead of a fixed 30 s. A submitted job
/// waits up to one <see cref="JobExecutionService.PollInterval"/> before a worker claims it (the
/// claim loop has no wake-up signal), and any single Redis call on the test host may stall for up
/// to <see cref="GPServerRedisTestConnection.StallTolerance"/> before it fails. Before, the job
/// waits and several client timeouts were 30 s, the same as the tolerated stall, so one stall the
/// host was configured to survive could still fail the test. A timeout reports the last status
/// response and the durable job record, so a genuine hang stays diagnosable.
/// </remarks>
internal static class GPServerJobWait
{
    /// <summary>HTTP timeout for one request: one tolerated Redis stall plus headroom.</summary>
    public static readonly TimeSpan RequestTimeout = GPServerRedisTestConnection.StallTolerance + TimeSpan.FromSeconds(15);

    /// <summary>Budget for a job to reach a terminal status: one tolerated stall plus six idle claim polls.</summary>
    public static readonly TimeSpan JobBudget = GPServerRedisTestConnection.StallTolerance + (JobExecutionService.PollInterval * 6);

    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Polls a GeoServices REST job-status URL until the job is terminal and returns the terminal body.
    /// </summary>
    public static async Task<JsonDocument> UntilRestTerminalAsync(
        HttpClient client,
        string statusUrl,
        string jobId,
        IExecutionJobStore? jobStore = null)
    {
        var elapsed = Stopwatch.StartNew();
        string? lastObserved = null;
        while (elapsed.Elapsed < JobBudget)
        {
            using var response = await client.GetAsync(statusUrl);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            lastObserved = body;

            using var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("jobStatus", out var status).Should()
                .BeTrue($"a job-status response must carry jobStatus: {body}");
            if (IsTerminal(status.GetString()))
            {
                return JsonDocument.Parse(body);
            }

            await Task.Delay(PollDelay);
        }

        throw await TimedOutAsync(jobId, lastObserved, jobStore);
    }

    /// <summary>
    /// Polls a GeoServices REST job-status URL until the job is terminal and asserts it succeeded.
    /// </summary>
    public static async Task<JsonDocument> UntilRestSucceededAsync(
        HttpClient client,
        string statusUrl,
        string jobId,
        IExecutionJobStore? jobStore = null)
    {
        var terminal = await UntilRestTerminalAsync(client, statusUrl, jobId, jobStore);
        terminal.RootElement.GetProperty("jobStatus").GetString()
            .Should().Be("esriJobSucceeded", terminal.RootElement.GetRawText());
        return terminal;
    }

    /// <summary>
    /// Polls a SOAP <c>GetJobStatus</c> reader until the job is terminal and asserts it succeeded.
    /// </summary>
    public static async Task UntilSoapSucceededAsync(
        Func<Task<string>> readStatus,
        string jobId,
        IExecutionJobStore? jobStore = null)
    {
        var elapsed = Stopwatch.StartNew();
        string? lastObserved = null;
        while (elapsed.Elapsed < JobBudget)
        {
            var status = await readStatus();
            lastObserved = status;
            if (IsTerminal(status))
            {
                status.Should().Be("esriJobSucceeded");
                return;
            }

            await Task.Delay(PollDelay);
        }

        throw await TimedOutAsync(jobId, lastObserved, jobStore);
    }

    private static bool IsTerminal(string? status)
        => status is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled" or "esriJobTimedOut";

    private static async Task<TimeoutException> TimedOutAsync(string jobId, string? lastObserved, IExecutionJobStore? jobStore)
    {
        var durable = "not inspected";
        if (jobStore is not null)
        {
            var record = await jobStore.GetAsync(jobId);
            durable = record is null
                ? "absent from the job store"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"status={record.Status}, attempts={record.AttemptCount}, claimedBy={record.ClaimedBy ?? "-"}, " +
                    $"phase={record.CurrentPhase ?? "-"}, updatedAt={record.UpdatedAt:O}, error={record.ErrorMessage ?? "-"}");
        }

        return new TimeoutException(
            $"GPServer job '{jobId}' did not reach a terminal status within {JobBudget.TotalSeconds:F0} s. " +
            $"Last status observed: {lastObserved ?? "none"}. Durable record: {durable}.");
    }
}
