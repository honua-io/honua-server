// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Honua.Core.Features.Security.Admission;
using StackExchange.Redis;

namespace Honua.Infrastructure.Security.Admission;

/// <summary>Atomic, fail-closed AI invocation authority backed by one non-expiring Redis hash.</summary>
internal sealed partial class RedisAiExecutionAdmissionStore(IConnectionMultiplexer connection) : IAiExecutionAdmissionStore
{
    private readonly IConnectionMultiplexer _connection = connection;

    internal const int MaximumControlEvents = 1024;
    internal const int ControlRetentionSeconds = 30 * 24 * 60 * 60;

    internal static RedisKey LedgerKey(string deploymentId)
        => "honua:ai-admission:" + Digest(deploymentId);

    public async Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, CancellationToken cancellationToken)
    {
        if (!context.IsValid || attemptId == Guid.Empty)
        {
            return new("invalid_context");
        }

        var result = await ExecuteAsync(context.DeploymentId,
            ["acquire", attemptId.ToString("N"), context.ActorKey, context.UseCase, context.WorkflowKey ?? ""], cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new("unavailable");
        }
        if (result.Code != "admitted" || result.Reservation is not { } granted)
        {
            return new(result.Code);
        }

        return new("admitted", new AiAdmissionReservation(context.DeploymentId, attemptId, Guid.Parse(granted.Generation), granted.Epoch,
            DateTimeOffset.FromUnixTimeMilliseconds(granted.Deadline), DateTimeOffset.FromUnixTimeMilliseconds(granted.Lease)));
    }

    public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, AiFinancialRequest request, CancellationToken cancellationToken)
        => TryAcquireAsync(context, attemptId, cancellationToken);
    public Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<string> SettleFinancialAsync(AiAdmissionReservation reservation, AiFinancialUsage usage, CancellationToken cancellationToken) => Task.FromResult("financial_unsupported");

    public async Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(reservation.DeploymentId,
            ["current", reservation.AttemptId.ToString("N"), reservation.Generation.ToString("N"), reservation.Epoch,
                reservation.DeadlineUtc.ToUnixTimeMilliseconds(), reservation.LeaseExpiresUtc.ToUnixTimeMilliseconds()], cancellationToken).ConfigureAwait(false);
        return result?.Code == "current";
    }

    public async Task ReleaseAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
    {
        // The generation, attempt, epoch and fixed deadlines all fence a release. Charges are never refunded.
        await ExecuteAsync(reservation.DeploymentId,
            ["release", reservation.AttemptId.ToString("N"), reservation.Generation.ToString("N"), reservation.Epoch,
                reservation.DeadlineUtc.ToUnixTimeMilliseconds(), reservation.LeaseExpiresUtc.ToUnixTimeMilliseconds()], cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiAdmissionState?> GetStateAsync(string deploymentId, CancellationToken cancellationToken)
        => ToState(deploymentId, (await ExecuteAsync(deploymentId, ["state"], cancellationToken).ConfigureAwait(false))?.State);

    public async Task<AiAdmissionControlResult> ApplyControlAsync(AiAdmissionControlCommand command, CancellationToken cancellationToken)
    {
        if (command.Policy?.Financial is not null) return new("financial_unsupported", null);
        if (!command.IsValid || Encoding.UTF8.GetByteCount(command.ActorId) > 4096
            || Encoding.UTF8.GetByteCount(command.InvocationId) > 128 || Encoding.UTF8.GetByteCount(command.CorrelationId) > 128)
        {
            return new("invalid_control", null);
        }

        var policy = command.Policy is null ? "" : JsonSerializer.Serialize(command.Policy, RedisAiAdmissionJsonContext.Default.AiAdmissionPolicy);
        var fingerprint = Digest(JsonSerializer.Serialize(command, RedisAiAdmissionJsonContext.Default.AiAdmissionControlCommand));
        var result = await ExecuteAsync(command.DeploymentId,
            ["control", command.Action.ToString(), command.ExpectedGeneration.ToString("N"), command.ExpectedRevision, command.ExpectedEpoch,
                command.ActorId, command.InvocationId, command.CorrelationId, policy, Guid.NewGuid().ToString("N"), fingerprint,
                Digest(command.InvocationId)], cancellationToken).ConfigureAwait(false);
        return result is null ? new("unavailable", null) : new(result.Code, ToState(command.DeploymentId, result.State), result.Applied);
    }

    private async Task<ScriptReply?> ExecuteAsync(string deploymentId, RedisValue[] arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deploymentId) || deploymentId.Length > 128)
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(arguments[0] == "current" ? TimeSpan.FromMilliseconds(AiAdmissionPolicy.StopPollMilliseconds) : TimeSpan.FromSeconds(3));
        var token = timeout.Token;
        try
        {
            // Cancellation after dispatch is ambiguous: deny rather than retrying a charged invocation.
            var database = _connection.GetDatabase();
            var key = LedgerKey(deploymentId);
            var authority = await InspectAuthorityAsync(database, key, token).ConfigureAwait(false);
            if (authority is null)
            {
                return null;
            }
            var boundArguments = arguments.Concat(new RedisValue[] { authority.RunId, authority.ReplicationId }).ToArray();
            var raw = await database.ScriptEvaluateAsync(Script, [key], boundArguments, AuthorityFlags)
                .WaitAsync(token).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize((string?)raw ?? "", RedisAiAdmissionJsonContext.Default.ScriptReply);
            // A changed owner, process, replication lineage or persistence policy makes the result
            // ambiguous. Never authorize a provider request from that result or retry under a new ID.
            var after = await InspectAuthorityAsync(database, key, token).ConfigureAwait(false);
            return IsValidReply(reply) && authority == after ? reply : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is RedisException or JsonException or FormatException or InvalidOperationException)
        {
            // Connection errors, corrupt state and ambiguous outcomes never select another authority.
            return null;
        }
    }

    private static AiAdmissionState? ToState(string deploymentId, StateRecord? state)
        => state is null || !IsValidState(state) ? null : new(deploymentId, state.Policy, Guid.ParseExact(state.Generation, "N"), state.Revision, state.Epoch,
            state.Stopped, DateTimeOffset.FromUnixTimeMilliseconds(state.NotBefore));

    private static bool IsValidState(StateRecord state)
        => state.Schema == 1 && state.Policy is { IsValid: true } && Guid.TryParseExact(state.Generation, "N", out var generation)
            && generation != Guid.Empty && state.Revision > 0 && state.Epoch > 0 && IsValidTimestamp(state.NotBefore);

    private static bool IsValidTimestamp(long milliseconds) => milliseconds is >= 0 and <= 253402300799999;

    private static bool IsValidReply(ScriptReply? reply)
    {
        if (reply is null || string.IsNullOrWhiteSpace(reply.Code) || (reply.State is not null && !IsValidState(reply.State))
            || (reply.Code == "admitted" && reply.Reservation is null) || (reply.Applied && reply.State is null))
        {
            return false;
        }
        return reply.Reservation is not { } reservation || (Guid.TryParseExact(reservation.Generation, "N", out var generation)
            && generation != Guid.Empty && reservation.Epoch > 0 && IsValidTimestamp(reservation.Deadline)
            && reservation.Deadline <= 253402300769999 && reservation.Lease == reservation.Deadline + 30000);
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal sealed record StateRecord(int Schema, string Generation, AiAdmissionPolicy Policy, long Revision, long Epoch, bool Stopped, long NotBefore);
    internal sealed record ReservationRecord(string Generation, long Epoch, long Deadline, long Lease);
    internal sealed record ScriptReply(string Code, StateRecord? State = null, ReservationRecord? Reservation = null, bool Applied = false);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AiAdmissionPolicy))]
[JsonSerializable(typeof(AiAdmissionControlCommand))]
[JsonSerializable(typeof(RedisAiExecutionAdmissionStore.ScriptReply))]
internal sealed partial class RedisAiAdmissionJsonContext : JsonSerializerContext;
