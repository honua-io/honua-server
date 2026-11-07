// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

public static class MigrationMetricsAmbient
{
    private static readonly AsyncLocal<MigrationRunMetricsRecorder?> CurrentRecorder = new();

    public static MigrationRunMetricsRecorder? Current => CurrentRecorder.Value;

    public static Scope PushRecorder(MigrationRunMetricsRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var previous = CurrentRecorder.Value;
        CurrentRecorder.Value = recorder;
        return new Scope(previous);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly MigrationRunMetricsRecorder? _previous;

        internal Scope(MigrationRunMetricsRecorder? previous)
        {
            _previous = previous;
        }

        public void Dispose() => CurrentRecorder.Value = _previous;
    }
}

// Mounted inside the resilience pipeline so each physical retry is counted, not only the logical operation.
public sealed partial class MigrationRequestCountingHandler : DelegatingHandler
{
    private readonly ILogger<MigrationRequestCountingHandler> _logger;

    public MigrationRequestCountingHandler(ILogger<MigrationRequestCountingHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var recorder = MigrationMetricsAmbient.Current;
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Count the request even when it failed at the transport layer — the source still saw the load.
            recorder?.RecordSourceRequest();
            throw;
        }

        if (recorder is null)
        {
            return response;
        }

        var bytesRead = TryReadContentLength(response);
        try
        {
            recorder.RecordSourceRequest(count: 1, bytesRead: bytesRead);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: metrics recording must never affect the live import
            // path — log and continue rather than let a recorder failure fault the response.
            LogRecordFailure(_logger, ex);
        }

        return response;
    }

    private static long TryReadContentLength(HttpResponseMessage response)
    {
        if (response.Content is null) return 0;
        var headerLength = response.Content.Headers.ContentLength;
        return headerLength is { } value && value > 0 ? value : 0;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to record migration source request metric; continuing without metric attribution.")]
    private static partial void LogRecordFailure(ILogger logger, Exception exception);
}
