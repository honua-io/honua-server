// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Admin.Features.Auth.Services;
using Honua.Admin.Features.GitOps.Models;

namespace Honua.Admin.Features.GitOps.Services;

internal sealed class GitOpsAdminClient
{
    private readonly HttpClient _httpClient;
    private readonly AuthStateStore _authStateStore;

    public GitOpsAdminClient(HttpClient httpClient, AuthStateStore authStateStore)
    {
        _httpClient = httpClient;
        _authStateStore = authStateStore;
    }

    public async Task<GitOpsWatchConfigModel?> GetWatchAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "/api/v1/admin/gitops/watch", cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadAsync(response, GitOpsAdminJsonContext.Default.GitOpsWatchConfigEnvelope, cancellationToken).ConfigureAwait(false);
        return envelope.Data;
    }

    public async Task<GitOpsWatchConfigModel> SaveWatchAsync(
        GitOpsWatchConfigSaveRequest requestModel,
        bool updateExisting,
        CancellationToken cancellationToken = default)
    {
        var method = updateExisting ? HttpMethod.Put : HttpMethod.Post;
        using var request = await CreateRequestAsync(method, "/api/v1/admin/gitops/watch", cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(requestModel, GitOpsAdminJsonContext.Default.GitOpsWatchConfigSaveRequest);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await ReadAsync(response, GitOpsAdminJsonContext.Default.GitOpsWatchConfigEnvelope, cancellationToken).ConfigureAwait(false);
        return envelope.Data ?? throw new InvalidOperationException("GitOps watch response did not include configuration data.");
    }

    public async Task DeleteWatchAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, "/api/v1/admin/gitops/watch", cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitOpsChangeRecordModel>> GetChangesAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "/api/v1/admin/gitops/changes?limit=100&offset=0", cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await ReadAsync(response, GitOpsAdminJsonContext.Default.GitOpsChangeListEnvelope, cancellationToken).ConfigureAwait(false);
        return envelope.Data ?? Array.Empty<GitOpsChangeRecordModel>();
    }

    public async Task<GitOpsChangeDiffModel?> GetDiffAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"/api/v1/admin/gitops/changes/{changeId}/diff", cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadAsync(response, GitOpsAdminJsonContext.Default.GitOpsChangeDiffEnvelope, cancellationToken).ConfigureAwait(false);
        return envelope.Data;
    }

    public async Task<IReadOnlyList<ManifestPendingChangeModel>> GetPendingApprovalsAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "/api/v1/admin/manifest/pending/", cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await ReadAsync(response, GitOpsAdminJsonContext.Default.ManifestPendingListEnvelope, cancellationToken).ConfigureAwait(false);
        return envelope.Data ?? Array.Empty<ManifestPendingChangeModel>();
    }

    public async Task<IReadOnlyList<ManifestPendingChangeModel>> GetApprovalHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "/api/v1/admin/manifest/pending/history", cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await ReadAsync(response, GitOpsAdminJsonContext.Default.ManifestPendingListEnvelope, cancellationToken).ConfigureAwait(false);
        return envelope.Data ?? Array.Empty<ManifestPendingChangeModel>();
    }

    public async Task<ManifestPendingChangeModel> ApproveAsync(
        Guid pendingId,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        return await SendDecisionAsync(
            pendingId,
            $"/api/v1/admin/manifest/pending/{pendingId}/approve",
            JsonContent.Create(
                new ManifestApproveRequestModel
                {
                    ApprovedBy = actor,
                    Reason = reason
                },
                GitOpsAdminJsonContext.Default.ManifestApproveRequestModel),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManifestPendingChangeModel> RejectAsync(
        Guid pendingId,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        return await SendDecisionAsync(
            pendingId,
            $"/api/v1/admin/manifest/pending/{pendingId}/reject",
            JsonContent.Create(
                new ManifestRejectRequestModel
                {
                    RejectedBy = actor,
                    Reason = reason
                },
                GitOpsAdminJsonContext.Default.ManifestRejectRequestModel),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManifestPendingChangeModel> SendDecisionAsync(
        Guid pendingId,
        string uri,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, uri, cancellationToken).ConfigureAwait(false);
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await ReadAsync(response, GitOpsAdminJsonContext.Default.ManifestPendingEnvelope, cancellationToken).ConfigureAwait(false);
        return envelope.Data ?? throw new InvalidOperationException($"Approval response for '{pendingId}' did not include updated change data.");
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string uri, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        var token = await _authStateStore.GetAccessTokenAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<ApiEnvelope<T>> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiEnvelope<T>> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The server returned an empty JSON payload.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(ExtractMessage(response, body));
    }

    private static string ExtractMessage(HttpResponseMessage response, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                }

                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                }

                if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                {
                    return title.GetString() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                }
            }
            catch (JsonException)
            {
                return body;
            }
        }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }
}
