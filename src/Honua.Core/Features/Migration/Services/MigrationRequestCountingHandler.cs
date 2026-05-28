// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Ambient flow that lets per-request HTTP handlers locate the <see cref="MigrationRunMetricsRecorder"/>
/// associated with the active migration run without threading the recorder through every API.
/// </summary>
/// <remarks>
/// The recorder is stored in an <see cref="AsyncLocal{T}"/>, so it follows the logical call chain across
/// async hops. Callers wrap a migration phase with <see cref="PushRecorder(MigrationRunMetricsRecorder)"/>
/// and dispose the returned scope when the phase ends. HTTP message handlers read the ambient recorder
/// via <see cref="Current"/>.
/// </remarks>
public static class MigrationMetricsAmbient
{
    private static readonly AsyncLocal<MigrationRunMetricsRecorder?> CurrentRecorder = new();

    /// <summary>Returns the recorder currently associated with the executing logical call context, if any.</summary>
    public static MigrationRunMetricsRecorder? Current => CurrentRecorder.Value;

    /// <summary>
    /// Associates <paramref name="recorder"/> with the current logical call context. Dispose the returned
    /// scope to restore the previously active recorder.
    /// </summary>
    public static Scope PushRecorder(MigrationRunMetricsRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var previous = CurrentRecorder.Value;
        CurrentRecorder.Value = recorder;
        return new Scope(previous);
    }

    /// <summary>Disposable scope returned by <see cref="PushRecorder(MigrationRunMetricsRecorder)"/>.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly MigrationRunMetricsRecorder? _previous;

        internal Scope(MigrationRunMetricsRecorder? previous)
        {
            _previous = previous;
        }

        /// <summary>Restores the previously active recorder.</summary>
        public void Dispose() => CurrentRecorder.Value = _previous;
    }
}

/// <summary>
/// <see cref="DelegatingHandler"/> that records one source request — and, when cheap to read, the response
/// payload byte count — with the ambient <see cref="MigrationRunMetricsRecorder"/> for every HTTP call
/// flowing through the GeoServer/ArcGIS migration clients.
/// </summary>
/// <remarks>
/// Mounted via <c>AddHttpMessageHandler&lt;MigrationRequestCountingHandler&gt;()</c> so the resilience
/// pipeline can retry the inner request as needed and each physical send is counted. When no recorder is
/// currently active (e.g. health probes or out-of-band calls) the handler simply forwards the request.
/// </remarks>
public sealed partial class MigrationRequestCountingHandler : DelegatingHandler
{
    private readonly ILogger<MigrationRequestCountingHandler> _logger;

    /// <summary>Initializes a new <see cref="MigrationRequestCountingHandler"/>.</summary>
    public MigrationRequestCountingHandler(ILogger<MigrationRequestCountingHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
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
        catch (Exception ex)
        {
            // Metrics recording must never affect the live import path.
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
