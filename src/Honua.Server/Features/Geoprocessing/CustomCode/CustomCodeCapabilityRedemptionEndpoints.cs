// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Geoprocessing.CustomCode;

// A custom-code container receives a single-use code, never its capability: provider job
// descriptions are readable by anyone allowed to describe jobs in the account. The code is
// spent by the first exchange, so a copy read after the job started is worthless, and a copy
// redeemed by someone else leaves the real job unable to start, which fails it visibly.
internal static class CustomCodeCapabilityRedemptionEndpoints
{
    internal const string Route = "/api/geoprocessing/jobs/{jobId}/attempts/{attempt}/capability";
    internal const string CodeHeader = "X-Honua-Redemption-Code";
    internal const string RedemptionCodeParameter = "env.HONUA_JOB_REDEMPTION_CODE";
    private const string AttemptParameter = "env.HONUA_JOB_ATTEMPT";
    private const string JobIdParameter = "env.HONUA_JOB_ID";
    private const string StartDeadlineParameter = "env.HONUA_JOB_START_DEADLINE";
    private const int CodeLength = 64;
    private const int MaximumCasAttempts = 8;

    private enum Redeemability
    {
        Redeemable,
        NotYetSubmitted,
        Refused,
        Expired,
    }

    public static void MapCustomCodeCapabilityRedemptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, RedeemAsync)
            // The single-use code is the only credential; ordinary user or admin auth cannot redeem.
            .AllowAnonymous()
            .WithTags("Geoprocessing")
            .WithName("RedeemCustomCodeJobCapability")
            .WithSummary("Exchange a custom-code job attempt's single-use code for its job capability")
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> RedeemAsync(string jobId, int attempt, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var codes = context.Request.Headers[CodeHeader];
        if (codes.Count != 1 || codes[0] is not { Length: CodeLength } code || !code.All(char.IsAsciiHexDigitLower) || attempt < 1)
        {
            return Problem(context, 401, "A single-use redemption code for this job attempt is required.");
        }

        if (context.RequestServices.GetService<IExecutionJobStore>() is not { } jobs)
        {
            return Problem(context, 503, "The durable job store must be configured.");
        }

        for (var retry = 0; retry < MaximumCasAttempts; retry++)
        {
            var job = await jobs.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
            switch (Classify(job, new RedemptionClaim(jobId, attempt, code), DateTimeOffset.UtcNow))
            {
                case Redeemability.NotYetSubmitted:
                    return Problem(context, 409, "The job attempt is not dispatched yet; retry shortly.");
                case Redeemability.Expired:
                    return Problem(context, 410, "The job attempt can no longer start.");
                case Redeemability.Refused:
                    return Problem(context, 401, "The redemption code does not authorize this job attempt.");
            }

            var spent = new Dictionary<string, string>(job!.Spec.Parameters, StringComparer.Ordinal);
            spent.Remove(RedemptionCodeParameter);
            if (await jobs.TrySetAsync(job with { Spec = job.Spec with { Parameters = spent } }, cancellationToken: context.RequestAborted).ConfigureAwait(false))
            {
                return Results.Text(job.Spec.Parameters[CustomCodeJobContract.JobTokenEnvParam], "text/plain", Encoding.UTF8);
            }
        }

        return Problem(context, 409, "Concurrent job changes prevented the redemption; retry shortly.");
    }

    private static Redeemability Classify(ExecutionJobRecord? job, RedemptionClaim claim, DateTimeOffset now)
    {
        var (jobId, attempt, code) = claim;
        if (job is null || job.Audit.CustomCodeOwnerScope is null
            || job.Spec.Kind != ExecutionJobKind.Geoprocessing || job.Spec.RuntimeProfile != CustomCodeJobContract.RuntimeProfile
            || !Matches(job, JobIdParameter, jobId) || !Matches(job, AttemptParameter, attempt.ToString(CultureInfo.InvariantCulture))
            || !job.Spec.Parameters.TryGetValue(RedemptionCodeParameter, out var stored) || !SameCode(stored, code)
            || !job.Spec.Parameters.TryGetValue(CustomCodeJobContract.JobTokenEnvParam, out var token) || string.IsNullOrEmpty(token))
        {
            return Redeemability.Refused;
        }

        // A pending retry means this attempt already ended; its code must not outlive it.
        if (job.Status.IsTerminal() || job.CancellationRequestedAt is not null || job.AttemptCount > attempt
            || job.NextRetryAt is not null
            || !job.Spec.Parameters.TryGetValue(StartDeadlineParameter, out var deadline)
            || !long.TryParse(deadline, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || now > DateTimeOffset.FromUnixTimeSeconds(seconds))
        {
            return Redeemability.Expired;
        }

        return job.AttemptCount == attempt && !string.IsNullOrEmpty(job.ProviderOperationId)
            && job.Status is ExecutionJobStatus.Queued or ExecutionJobStatus.Running
            ? Redeemability.Redeemable
            : Redeemability.NotYetSubmitted;
    }

    private sealed record RedemptionClaim(string JobId, int Attempt, string Code);

    private static bool Matches(ExecutionJobRecord job, string parameter, string expected)
        => job.Spec.Parameters.TryGetValue(parameter, out var value) && string.Equals(value, expected, StringComparison.Ordinal);

    private static bool SameCode(string stored, string presented)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(stored)), SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    private static IResult Problem(HttpContext context, int status, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, status, ProblemDetailsHelpers.GetTitle(status), detail);
}
