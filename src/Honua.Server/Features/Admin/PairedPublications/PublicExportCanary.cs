// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicExportCanaryResult
{
    public bool IsReadable { get; init; }

    public string? Detail { get; init; }

    public static PublicExportCanaryResult Readable { get; } = new() { IsReadable = true };

    public static PublicExportCanaryResult Unreadable(string detail) => new() { IsReadable = false, Detail = detail };
}

internal sealed record PublicExportCanaryFailure(string PublicationId, string Version, Uri PublicUrl, string Detail);

internal interface IPublicExportCanary
{
    Task<PublicExportCanaryResult> CheckAsync(Uri publicUrl, CancellationToken cancellationToken);
}

internal sealed class HttpPublicExportCanary(IHttpClientFactory httpClientFactory) : IPublicExportCanary
{
    internal const string HttpClientName = "PublicExportCanary";

    public async Task<PublicExportCanaryResult> CheckAsync(Uri publicUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publicUrl);
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, publicUrl);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? PublicExportCanaryResult.Readable
                : PublicExportCanaryResult.Unreadable($"Public URL returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return PublicExportCanaryResult.Unreadable($"Public URL request failed: {exception.Message}");
        }
    }
}

internal interface IPublicExportOpsNotifier
{
    Task NotifyCanaryFailedAsync(PublicExportCanaryFailure failure, CancellationToken cancellationToken);

    Task NotifyRevocationFailedAsync(PublicExportRevocationRecord record, string detail, CancellationToken cancellationToken);
}

internal sealed partial class OpsNotificationPublicExportNotifier(
    IServiceScopeFactory scopeFactory,
    ILogger<OpsNotificationPublicExportNotifier> logger) : IPublicExportOpsNotifier
{
    internal const string CanarySource = "public-export-canary";
    internal const string RevocationSource = "public-export-revocation";
    internal const string CanaryFindingTitle = "Public data unreadable through the CDN";

    public Task NotifyCanaryFailedAsync(PublicExportCanaryFailure failure, CancellationToken cancellationToken)
        => NotifyAsync(new OpsNotification
        {
            Source = CanarySource,
            DedupeIdentifier = $"{failure.PublicationId}:{failure.Version}",
            Severity = AlertSeverity.Warning,
            Title = CanaryFindingTitle,
            Body = $"Export '{failure.PublicationId}' version '{failure.Version}' was written to public storage, but an anonymous fetch of {failure.PublicUrl} failed ({failure.Detail}). Check the public bucket policy, encryption key permissions, and CDN origin access.",
            Attributes = ImmutableDictionary<string, string>.Empty
                .Add("publicationId", failure.PublicationId)
                .Add("version", failure.Version)
                .Add("publicUrl", failure.PublicUrl.AbsoluteUri)
                .Add("detail", failure.Detail)
        }, cancellationToken);

    public Task NotifyRevocationFailedAsync(
        PublicExportRevocationRecord record, string detail, CancellationToken cancellationToken)
        => NotifyAsync(new OpsNotification
        {
            Source = RevocationSource,
            DedupeIdentifier = $"{record.RevocationId}:{record.State}",
            Severity = AlertSeverity.Critical,
            Title = "Public export revocation failed and will retry",
            Body = $"Revocation '{record.RevocationId}' for export '{record.PublicationId}' (reason: {record.Reason}) failed at step '{record.State}': {detail}. Honua will retry automatically.",
            CorrelationId = record.ChangeId,
            Attributes = ImmutableDictionary<string, string>.Empty
                .Add("revocationId", record.RevocationId)
                .Add("publicationId", record.PublicationId)
                .Add("state", record.State.ToString())
                .Add("detail", detail)
        }, cancellationToken);

    private async Task NotifyAsync(OpsNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var ops = scope.ServiceProvider.GetService<OpsNotificationService>();
            if (ops is not null)
            {
                await ops.NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOpsNotificationFailed(logger, notification.Source, notification.DedupeIdentifier, exception);
        }
    }

    [LoggerMessage(EventId = 10342, Level = LogLevel.Warning,
        Message = "Failed to post ops notification for {Source} ({Dedupe}).")]
    private static partial void LogOpsNotificationFailed(ILogger logger, string source, string dedupe, Exception exception);
}
