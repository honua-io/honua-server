// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks.Features.Infrastructure;

internal sealed record DatabricksResultSet(IReadOnlyList<string> Columns, IReadOnlyList<JsonElement[]> Rows);

internal interface IDatabricksStatementClient
{
    Task<DatabricksResultSet> ExecuteAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken);
}

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
            // When the whole command budget fits in that first wait, ask Databricks to
            // cancel the statement instead of leaving it running.
            OnWaitTimeout = _options.CommandTimeoutSeconds <= 50 ? "CANCEL" : null,
            Parameters = statement.Parameters.Count == 0
                ? null
                : statement.Parameters
                    .Select(p => new DatabricksStatementParameter { Name = p.Name, Value = p.Value, Type = p.Type })
                    .ToList(),
        };

        var response = await PostAsync(request, cancellationToken).ConfigureAwait(false);
        var statementId = response.StatementId;
        try
        {
            response = await AwaitCompletionAsync(response, cancellationToken).ConfigureAwait(false);
            statementId = response.StatementId ?? statementId;

            var columns = ResolveColumns(response);
            var rows = await CollectRowsAsync(response, cancellationToken).ConfigureAwait(false);
            return new DatabricksResultSet(columns, rows);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(statementId))
            {
                await CancelStatementAsync(statementId).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task CancelStatementAsync(string statementId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                StatusPathPrefix + Uri.EscapeDataString(statementId) + "/cancel");
            ApplyAuthorization(message);
            using var httpResponse = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Cancellation is best-effort. The caller still observes the original timeout or abort.
        }
        catch (OperationCanceledException)
        {
            // The cancel call has its own timeout and must not replace the original failure.
        }
        catch (InvalidOperationException)
        {
            // A non-success cancel response is ignored; the original failure is rethrown.
        }
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

internal static class DatabricksServiceClientName
{
    public const string Default = "Honua.Databricks.Statements";

    public const string ResilienceServiceType = "databricks-sql";
}
