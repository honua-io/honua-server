// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Databricks.Features.FeatureStore.Services;
using Microsoft.Extensions.Options;

namespace Honua.Databricks.Features.Infrastructure;

/// <summary>
/// A fully materialized result set from the Statement Execution API: ordered column
/// names plus the row data (each cell as a JSON element).
/// </summary>
/// <param name="Columns">Ordered column names.</param>
/// <param name="Rows">Row data; each row aligns positionally with <paramref name="Columns"/>.</param>
internal sealed record DatabricksResultSet(IReadOnlyList<string> Columns, IReadOnlyList<JsonElement[]> Rows);

/// <summary>
/// Submits SQL statements to the Databricks SQL Statement Execution REST API, polls
/// for completion, and pages through inline result chunks.
/// </summary>
internal interface IDatabricksStatementClient
{
    /// <summary>
    /// Executes <paramref name="statement"/> and returns the fully materialized result set.
    /// </summary>
    Task<DatabricksResultSet> ExecuteAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken);
}

/// <summary>
/// HttpClient-backed implementation of <see cref="IDatabricksStatementClient"/>.
/// </summary>
/// <remarks>
/// Statement Execution API flow (REST, JSON, bearer auth):
/// <list type="number">
///   <item>POST <c>/api/2.0/sql/statements/</c> with the SQL, warehouse id, parameters,
///         <c>disposition=INLINE</c>, <c>format=JSON_ARRAY</c>, and a server-side
///         <c>wait_timeout</c>.</item>
///   <item>If the response state is <c>PENDING</c>/<c>RUNNING</c>, poll
///         GET <c>/api/2.0/sql/statements/{id}</c> until <c>SUCCEEDED</c> (or the
///         client budget elapses).</item>
///   <item>Read inline <c>result.data_array</c> rows and follow
///         <c>result.next_chunk_internal_link</c> to page additional chunks.</item>
/// </list>
/// The bearer token is sent only via the <c>Authorization</c> request header — never in
/// a URL — and provider errors are surfaced as <see cref="InvalidOperationException"/>
/// (mapped by shared problem helpers above this layer); tokens, URLs, and raw provider
/// internals are never echoed to clients.
/// </remarks>
internal sealed class DatabricksStatementClient : IDatabricksStatementClient
{
    private const string SubmitPath = "/api/2.0/sql/statements/";
    private const string StatusPathPrefix = "/api/2.0/sql/statements/";

    private readonly HttpClient _httpClient;
    private readonly DatabricksOptions _options;

    public DatabricksStatementClient(HttpClient httpClient, IOptions<DatabricksOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DatabricksResultSet> ExecuteAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var request = new DatabricksStatementRequest
        {
            Statement = statement.Sql,
            WarehouseId = _options.WarehouseId,
            Catalog = _options.Catalog,
            Schema = _options.Schema,
            // Cap the server-side wait at the API maximum (50s); the client poll loop
            // covers the remainder of the configured CommandTimeout budget.
            WaitTimeout = "50s",
            Parameters = statement.Parameters.Count == 0
                ? null
                : statement.Parameters
                    .Select(p => new DatabricksStatementParameter { Name = p.Name, Value = p.Value, Type = p.Type })
                    .ToList(),
        };

        var response = await PostAsync(request, cancellationToken).ConfigureAwait(false);
        response = await AwaitCompletionAsync(response, cancellationToken).ConfigureAwait(false);

        var columns = ResolveColumns(response);
        var rows = await CollectRowsAsync(response, cancellationToken).ConfigureAwait(false);
        return new DatabricksResultSet(columns, rows);
    }

    private async Task<DatabricksStatementResponse> PostAsync(DatabricksStatementRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, SubmitPath)
        {
            Content = JsonContent.Create(request, DatabricksJsonContext.Default.DatabricksStatementRequest),
        };
        ApplyAuthorization(message);

        using var httpResponse = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(httpResponse, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DatabricksStatementResponse> AwaitCompletionAsync(DatabricksStatementResponse response, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.CommandTimeoutSeconds);
        var current = response;

        while (true)
        {
            var state = current.Status?.State?.ToUpperInvariant();
            switch (state)
            {
                case "SUCCEEDED":
                    return current;
                case "FAILED":
                case "CANCELED":
                case "CLOSED":
                    throw BuildError(current);
                case "PENDING":
                case "RUNNING":
                case null:
                    break;
                default:
                    throw new InvalidOperationException($"Databricks statement returned unexpected state '{state}'.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Databricks statement did not complete within {_options.CommandTimeoutSeconds}s.");
            }

            await Task.Delay(_options.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);

            var statementId = current.StatementId
                ?? throw new InvalidOperationException("Databricks statement response did not include a statement id to poll.");
            current = await GetStatusAsync(statementId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DatabricksStatementResponse> GetStatusAsync(string statementId, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, StatusPathPrefix + Uri.EscapeDataString(statementId));
        ApplyAuthorization(message);

        using var httpResponse = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(httpResponse, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<JsonElement[]>> CollectRowsAsync(DatabricksStatementResponse response, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement[]>();
        AppendRows(rows, response.Result);

        var nextLink = response.Result?.NextChunkInternalLink;
        while (!string.IsNullOrWhiteSpace(nextLink))
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, nextLink);
            ApplyAuthorization(message);

            using var httpResponse = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var chunk = await ReadResponseAsync(httpResponse, cancellationToken).ConfigureAwait(false);

            AppendRows(rows, chunk.Result);
            nextLink = chunk.Result?.NextChunkInternalLink;
        }

        return rows;
    }

    private static void AppendRows(List<JsonElement[]> rows, DatabricksStatementResult? result)
    {
        if (result?.DataArray is { Length: > 0 } data)
        {
            rows.AddRange(data);
        }
    }

    private static List<string> ResolveColumns(DatabricksStatementResponse response)
    {
        var columns = response.Manifest?.Schema?.Columns;
        if (columns is not { Length: > 0 })
        {
            return [];
        }

        return columns
            .OrderBy(static c => c.Position)
            .Select(static c => c.Name)
            .ToList();
    }

    private void ApplyAuthorization(HttpRequestMessage message)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
    }

    private static async Task<DatabricksStatementResponse> ReadResponseAsync(HttpResponseMessage httpResponse, CancellationToken cancellationToken)
    {
        if (!httpResponse.IsSuccessStatusCode)
        {
            // Surface only the HTTP status code; the upstream body may carry workspace
            // details, so it is not propagated to keep provider internals out of client errors.
            throw new InvalidOperationException(
                $"Databricks Statement Execution API returned HTTP {(int)httpResponse.StatusCode}.");
        }

        var result = await httpResponse.Content
            .ReadFromJsonAsync(DatabricksJsonContext.Default.DatabricksStatementResponse, cancellationToken)
            .ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("Databricks Statement Execution API returned an empty response body.");
    }

    private static InvalidOperationException BuildError(DatabricksStatementResponse response)
    {
        var error = response.Status?.Error;
        var code = string.IsNullOrWhiteSpace(error?.ErrorCode) ? response.Status?.State : error!.ErrorCode;
        var message = string.IsNullOrWhiteSpace(error?.Message) ? string.Empty : $": {error!.Message}";

        // Message is internal-facing; shared error mapping prevents leakage to HTTP clients.
        return new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Databricks statement failed ({code}){message}"));
    }
}

/// <summary>
/// Well-known DI client name registered through <c>AddHttpClient</c>. Lets host
/// applications attach handlers (resilience, retry, telemetry) without taking a hard
/// dependency on this provider's internals.
/// </summary>
internal static class DatabricksServiceClientName
{
    /// <summary>Logical HttpClient name for Databricks Statement Execution outbound calls.</summary>
    public const string Default = "Honua.Databricks.Statements";

    /// <summary>
    /// Service-type identifier used to isolate this provider's resilience (retry +
    /// circuit-breaker) state from other external dependencies.
    /// </summary>
    public const string ResilienceServiceType = "databricks-sql";
}
