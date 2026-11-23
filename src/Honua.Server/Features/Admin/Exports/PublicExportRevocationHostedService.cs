// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Exports;

internal sealed record PublicExportRevocationRequest(string PublicationId, string Reason, string ChangeId);

internal sealed record PublicExportRevocationDependencies
{
    public required IPublicExportStateStore StateStore { get; init; }
    public required PublicExportEligibility Eligibility { get; init; }
    public required IMetadataV2GraphProvider GraphProvider { get; init; }
    public required CdnInvalidationService CdnInvalidation { get; init; }
    public required IPublicExportOpsNotifier OpsNotifier { get; init; }
    public required IConfiguration Configuration { get; init; }
    public IPublicPublicationDestination? Destination { get; init; }
    public ICdnInvalidationStatusReader? StatusReader { get; init; }
    public IHttpContextAccessor? HttpContextAccessor { get; init; }
    public ICloudFileStorage? PrivateStorage { get; init; }
    public IOptions<CloudStorageOptions>? PrivateStorageOptions { get; init; }
}

// Resumes non-terminal public export revocations and periodically re-evaluates promoted exports.
internal sealed partial class PublicExportRevocationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PublicExportRevocationHostedService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan EligibilitySweepInterval = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? _lastEligibilitySweepAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            var keepRunning = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            if (!keepRunning)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task<bool> RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IPublicPublicationDestination>() is null)
            {
                return false;
            }

            var revocation = scope.ServiceProvider.GetService<PublicExportRevocationService>();
            if (revocation is null)
            {
                return true;
            }

            await TrySweepEligibilityAsync(revocation, stoppingToken).ConfigureAwait(false);
            await revocation.ResumePendingAsync(stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            LogRevocationSweepFailed(logger, exception);
            return true;
        }
    }

    private async Task TrySweepEligibilityAsync(
        PublicExportRevocationService revocation,
        CancellationToken stoppingToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_lastEligibilitySweepAt is { } last && now - last < EligibilitySweepInterval)
        {
            return;
        }

        try
        {
            // An export whose unpublish check failed was logged by the service and waits for the
            // next scheduled sweep; only a failure of the whole sweep retries on the next tick.
            _ = await revocation.SweepEligibilityAsync(stoppingToken).ConfigureAwait(false);
            _lastEligibilitySweepAt = _timeProvider.GetUtcNow();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEligibilitySweepFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 10344, Level = LogLevel.Warning,
        Message = "Public export revocation recovery sweep failed; pending revocations will retry on the next tick.")]
    private static partial void LogRevocationSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 10343, Level = LogLevel.Warning,
        Message = "Public export eligibility sweep failed; re-evaluation will retry on the next tick.")]
    private static partial void LogEligibilitySweepFailed(ILogger logger, Exception exception);
}
