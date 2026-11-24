// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Security;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks.Features.Infrastructure;

internal sealed record DatabricksResultSet(IReadOnlyList<string> Columns, IReadOnlyList<JsonElement[]> Rows);

internal sealed class DatabricksStatementClient
{
    private const string SubmitPath = "/api/2.0/sql/statements/";
    private const string StatusPathPrefix = "/api/2.0/sql/statements/";

    private sealed record ExecutionTarget(Uri WorkspaceBaseUri, string WarehouseId, string Token);

    private readonly HttpClient _httpClient;
    private readonly DatabricksOptions _options;
    private readonly ISecureConnectionResolver? _connectionResolver;

    public DatabricksStatementClient(
        HttpClient httpClient,
        IOptions<DatabricksOptions> options,
        ISecureConnectionResolver? connectionResolver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _connectionResolver = connectionResolver;
    }

    public Task<DatabricksResultSet> ExecuteAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken)
        => ExecuteAsync(statement, dataConnection: null, cancellationToken);

    public async Task<DatabricksResultSet> ExecuteAsync(
        DatabricksSqlStatement statement,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var target = await ResolveExecutionTargetAsync(dataConnection, cancellationToken).ConfigureAwait(false);

        var request = new DatabricksStatementRequest
        {
            Statement = statement.Sql,
            WarehouseId = target.WarehouseId,
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

        var response = await PostAsync(request, target, cancellationToken).ConfigureAwait(false);
        var statementId = response.StatementId;
        try
        {
            response = await AwaitCompletionAsync(response, target, cancellationToken).ConfigureAwait(false);
            statementId = response.StatementId ?? statementId;

            var columns = ResolveColumns(response);
            var rows = await CollectRowsAsync(response, target, cancellationToken).ConfigureAwait(false);
            return new DatabricksResultSet(columns, rows);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(statementId))
            {
                await CancelStatementAsync(statementId, target).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<ExecutionTarget> ResolveExecutionTargetAsync(
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        if (dataConnection is not null)
        {
            string? connectionString = null;
            if (dataConnection.ConnectionId != Guid.Empty && _connectionResolver is not null)
            {
                connectionString = await _connectionResolver
                    .ResolveConnectionStringAsync(dataConnection.ConnectionId, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        $"Bound Databricks connection '{dataConnection.ConnectionId}' could not be resolved to a connection string.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(dataConnection.ConnectionString))
            {
                if (dataConnection.IsEncrypted)
                {
                    throw new InvalidOperationException(
                        "Bound Databricks connection has an encrypted connection string that cannot be resolved without ISecureConnectionResolver.");
                }

                connectionString = dataConnection.ConnectionString;
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var (endpoint, warehouseId, token) = DatabricksConnectionDriver.ParseConnectionString(connectionString);
                return new ExecutionTarget(endpoint, warehouseId, token);
            }

            // Fail closed before comparing against the default workspace whenever the bound
            // DataConnection carries an unresolved secret reference or encrypted credential
            // payload so it can never silently execute under the host's default token.
            if (!string.IsNullOrWhiteSpace(dataConnection.SecretRef))
            {
                throw new InvalidOperationException(
                    "Bound Databricks connection has a secret reference that cannot be resolved without ISecureConnectionResolver.");
            }

            if (dataConnection.EncryptedConnectionString.Length > 0)
            {
                throw new InvalidOperationException(
                    "Bound Databricks connection has encrypted credentials that cannot be resolved without ISecureConnectionResolver.");
            }

            // Fail closed if the bound DataConnection names a different workspace host or warehouse
            // than the default options without providing resolvable credentials.
            var fallback = ResolveDefaultTarget();
            if (!MatchesDefaultTarget(dataConnection, fallback))
            {
                if (dataConnection.IsEncrypted)
                {
                    throw new InvalidOperationException(
                        "Bound Databricks connection has encrypted credentials that cannot be resolved.");
                }

                throw new InvalidOperationException(
                    $"Bound Databricks connection '{dataConnection.Name}' could not be resolved to workspace credentials.");
            }

            return fallback;
        }

        return ResolveDefaultTarget();
    }

    private ExecutionTarget ResolveDefaultTarget()
    {
        var baseUri = _httpClient.BaseAddress;
        if (baseUri is null && Uri.TryCreate(_options.Host, UriKind.Absolute, out var configuredHost))
        {
            baseUri = configuredHost;
        }

        if (baseUri is null)
        {
            throw new InvalidOperationException("Databricks statement execution requires a configured workspace host.");
        }

        return new ExecutionTarget(baseUri, _options.WarehouseId, _options.Token);
    }

    private static bool MatchesDefaultTarget(DataConnection dataConnection, ExecutionTarget defaultTarget)
    {
        if (string.IsNullOrWhiteSpace(defaultTarget.Token) || string.IsNullOrWhiteSpace(defaultTarget.WarehouseId))
        {
            return false;
        }

        var hasHost = !string.IsNullOrWhiteSpace(dataConnection.Host);
        var hasWarehouse = !string.IsNullOrWhiteSpace(dataConnection.DatabaseName);
        if (!hasHost && !hasWarehouse && dataConnection.ConnectionId != Guid.Empty)
        {
            return false;
        }

        if (hasHost)
        {
            var boundHost = dataConnection.Host.Trim();
            var defaultHost = defaultTarget.WorkspaceBaseUri.IdnHost;
            var defaultAuthority = defaultTarget.WorkspaceBaseUri.GetLeftPart(UriPartial.Authority);
            if (!boundHost.Equals(defaultHost, StringComparison.OrdinalIgnoreCase)
                && !boundHost.Equals(defaultAuthority, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (hasWarehouse
            && !dataConnection.DatabaseName.Trim().Equals(defaultTarget.WarehouseId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private async Task CancelStatementAsync(string statementId, ExecutionTarget target)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(target.WorkspaceBaseUri, StatusPathPrefix + Uri.EscapeDataString(statementId) + "/cancel"));
            ApplyAuthorization(message, target);
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

    private async Task<DatabricksStatementResponse> PostAsync(
        DatabricksStatementRequest request,
        ExecutionTarget target,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(target.WorkspaceBaseUri, SubmitPath))
        {
            Content = JsonContent.Create(request, DatabricksJsonContext.Default.DatabricksStatementRequest),
        };
        ApplyAuthorization(message, target);

        using var httpResponse = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(httpResponse, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DatabricksStatementResponse> AwaitCompletionAsync(
        DatabricksStatementResponse response,
        ExecutionTarget target,
        CancellationToken cancellationToken)
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
            current = await GetStatusAsync(statementId, target, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DatabricksStatementResponse> GetStatusAsync(
        string statementId,
        ExecutionTarget target,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(target.WorkspaceBaseUri, StatusPathPrefix + Uri.EscapeDataString(statementId)));
        ApplyAuthorization(message, target);

        using var httpResponse = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(httpResponse, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<JsonElement[]>> CollectRowsAsync(
        DatabricksStatementResponse response,
        ExecutionTarget target,
        CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement[]>();
        AppendRows(rows, response.Result);

        var nextLink = response.Result?.NextChunkInternalLink;
        while (!string.IsNullOrWhiteSpace(nextLink))
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, ResolveChunkLink(nextLink, target.WorkspaceBaseUri));
            ApplyAuthorization(message, target);

            using var httpResponse = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var chunk = await ReadResponseAsync(httpResponse, cancellationToken).ConfigureAwait(false);

            AppendRows(rows, chunk.Result);
            nextLink = chunk.Result?.NextChunkInternalLink;
        }

        return rows;
    }

    // A chunk link is followed only on the resolved workspace origin, so the workspace token is
    // never attached to a request for another host or scheme.
    private static Uri ResolveChunkLink(string nextLink, Uri workspace)
    {
        var resolved = new Uri(workspace, nextLink);
        if (Uri.Compare(workspace, resolved, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException("Databricks returned a result chunk link outside the configured workspace host.");
        }

        return resolved;
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

    private static void ApplyAuthorization(HttpRequestMessage message, ExecutionTarget target)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
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
