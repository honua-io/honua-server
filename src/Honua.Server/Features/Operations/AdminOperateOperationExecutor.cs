// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Operations;

/// <summary>Thin executor that binds a release/operate operation to its existing Admin REST route.</summary>
internal sealed class AdminOperateOperationExecutor : IOperationExecutor
{
    public const string HttpClientName = "admin-operate-operation-loopback";
    private readonly IAdminHttpOperationDefinition _definition;
    private readonly IOperationDescriptor _descriptor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAdminApiKeyStore? _adminApiKeyStore;
    private readonly IOperationSecretStore? _operationSecretStore;
    private readonly TimeProvider _clock;
    private readonly OperationLineageAttestationStore _lineageAttestationStore;

    public AdminOperateOperationExecutor(IAdminHttpOperationDefinition definition, IOperationDescriptor descriptor,
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor, IAdminApiKeyStore? adminApiKeyStore, TimeProvider clock,
        OperationLineageAttestationStore lineageAttestationStore,
        IOperationSecretStore? operationSecretStore = null)
    {
        _definition = definition;
        _descriptor = descriptor;
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _adminApiKeyStore = adminApiKeyStore;
        _operationSecretStore = operationSecretStore;
        _clock = clock;
        _lineageAttestationStore = lineageAttestationStore;
    }

    public string OperationId => _definition.OperationId;

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var routeNames = RouteNames(_definition.Path);
        var missing = _descriptor.InputSchema
            .Where(static input => input.Required)
            .Where(input => string.IsNullOrWhiteSpace(request.Parameters.GetValueOrDefault(input.Name)))
            .Select(input => routeNames.Contains(input.Name)
                ? $"Required route parameter '{input.Name}' is missing."
                : $"Required parameter '{input.Name}' is missing.")
            .ToArray();
        return Task.FromResult(new OperationValidation
        {
            IsValid = missing.Length == 0,
            Status = missing.Length == 0 ? "valid" : "invalid",
            Messages = missing
        });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var current = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Admin operations require an active authenticated request.");
        request = OperationSecretParameters.Resolve(request, context, _operationSecretStore);
        if (SecretOutputName is not null && _operationSecretStore?.IsAvailable != true)
        {
            throw new InvalidOperationException(
                "Secret-bearing operation execution requires an available operation secret channel.");
        }
        var dryRun = request.DryRun && _definition.SupportsDryRun;
        var path = BindPath(request, dryRun ? _definition.DryRunPath! : _definition.Path);
        var method = dryRun ? _definition.DryRunMethod ?? _definition.Method : _definition.Method;
        var uri = BuildLocalUri(current, $"/api/v1/admin{AppendQuery(path, request)}");
        using var message = new HttpRequestMessage(method, uri);
        OperationLineageHeaders.Apply(message, context, _lineageAttestationStore);
        message.Headers.Host = current.Request.Host.Value;
        AdminApiKeyRecord? executionCredential = null;
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            var credentialStore = _adminApiKeyStore
                ?? throw new InvalidOperationException("Approved operation replay requires the admin API-key store.");
            var issued = await credentialStore.CreateAsync(
                $"approved-operation:{context.ApprovedProposalId}",
                [AdminApiKeyPermission.CreateApprovedOperationGrant(method.Method, uri.AbsolutePath)],
                _clock.GetUtcNow().AddMinutes(5),
                context.PrincipalId,
                cancellationToken).ConfigureAwait(false);
            executionCredential = issued.Record;
            message.Headers.TryAddWithoutValidation("X-API-Key", issued.Key);
            if (!string.IsNullOrWhiteSpace(context.TenantId))
                message.Headers.TryAddWithoutValidation("X-Honua-Tenant", context.TenantId);
        }
        else
        {
            if (current.Request.Headers.Authorization is { Count: > 0 } authorization)
                message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization.ToString());
            CopyHeader(current, message, "X-API-Key");
            CopyHeader(current, message, "X-Honua-Tenant");
        }

        if (method != HttpMethod.Get)
        {
            if (string.Equals(_definition.ContentType, "application/octet-stream", StringComparison.Ordinal))
            {
                message.Content = new StringContent(request.Parameters.GetValueOrDefault("body") ?? string.Empty, Encoding.UTF8, _definition.ContentType);
            }
            else
            {
                var routeNames = RouteNames(dryRun ? _definition.DryRunPath! : _definition.Path);
                message.Content = new StringContent(
                    SerializeBody(request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null)),
                    Encoding.UTF8,
                    "application/json");
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (executionCredential is not null)
                await ApprovedOperationCredentialRevocation.RevokeAsync(
                    _adminApiKeyStore!, executionCredential.Id).ConfigureAwait(false);
        }
        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var operationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}";
            var correlationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}";
            var now = _clock.GetUtcNow();
            OperationResultSummary result;
            try
            {
                result = BuildResult(payload, response.IsSuccessStatusCode, operationInstanceId, context);
            }
            catch (OperationSecretPersistenceException)
            {
                return new OperationHandle
                {
                    OperationInstanceId = operationInstanceId,
                    OperationId = OperationId,
                    CorrelationId = correlationId,
                    Status = OperationHandleStatus.Indeterminate,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Reason = "Admin API actuation succeeded, but its one-time secret could not be durably stored.",
                    Result = new OperationResultSummary
                    {
                        Summary = $"{_definition.Title} outcome is indeterminate.",
                        Details = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["response"] = SanitizeResponse(payload),
                        },
                    },
                };
            }
            if (!response.IsSuccessStatusCode)
            {
                return new OperationHandle
                {
                    OperationInstanceId = operationInstanceId,
                    OperationId = OperationId,
                    CorrelationId = correlationId,
                    Status = OperationHandleStatus.Failed,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Reason = $"Admin API returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                    Result = result with
                    {
                        Details = AddStatusCode(result.Details, (int)response.StatusCode),
                    }
                };
            }
            return new OperationHandle
            {
                OperationInstanceId = operationInstanceId,
                OperationId = OperationId,
                CorrelationId = correlationId,
                Status = OperationHandleStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
                Result = result,
            };
        }
    }

    private OperationResultSummary BuildResult(
        string payload,
        bool success,
        string operationInstanceId,
        OperationPolicyContext context)
    {
        var secretReferences = new List<OperationSecretReference>();
        var response = payload;
        if (RequiresSecretSanitization)
        {
            if (success && SecretOutputName is { } secretName &&
                TryGetResponseSecret(payload, secretName, out var secret))
            {
                var store = _operationSecretStore
                    ?? throw new InvalidOperationException("Secret-bearing operation result requires the operation secret channel.");
                try
                {
                    secretReferences.Add(store.Store(
                        operationInstanceId,
                        OperationId,
                        context.PrincipalId,
                        context.TenantId,
                        secretName,
                        secret));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    throw new OperationSecretPersistenceException();
                }
            }

            response = SanitizeResponse(payload);
        }

        return new OperationResultSummary
        {
            Summary = $"{_definition.Title} {(success ? "completed" : "failed")}.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["response"] = response },
            SecretReferences = secretReferences,
        };
    }

    private static Dictionary<string, string> AddStatusCode(
        IReadOnlyDictionary<string, string> details,
        int statusCode)
    {
        var result = details.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        result["statusCode"] = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }

    private bool RequiresSecretSanitization =>
        SecretOutputName is not null ||
        string.Equals(OperationId, "admin.oidc-provider.create", StringComparison.Ordinal) ||
        string.Equals(OperationId, "admin.oidc-provider.update", StringComparison.Ordinal);

    private string? SecretOutputName => OperationId switch
    {
        "admin.api-key.create" or "admin.api-key.rotate" => "key",
        "admin.oauth-client.register" => "clientSecret",
        _ => null,
    };

    private static bool TryGetResponseSecret(string payload, string name, out string secret)
    {
        secret = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var current = document.RootElement.TryGetProperty("data", out var data)
                ? data
                : document.RootElement;
            if (!current.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                return false;
            }

            secret = value.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SanitizeResponse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteSanitized(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static void WriteSanitized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "key", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(property.Name, "clientSecret", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteSanitized(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray())
                {
                    WriteSanitized(writer, value);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private sealed class OperationSecretPersistenceException : Exception;

    private static Uri BuildLocalUri(HttpContext current, string pathAndQuery)
    {
        var localPort = current.Connection.LocalPort;
        if (localPort <= 0)
            throw new InvalidOperationException("Admin operation loopback requires the local server port.");

        var scheme = current.Features.Get<Microsoft.AspNetCore.Http.Features.ITlsConnectionFeature>() is null
            ? Uri.UriSchemeHttp
            : Uri.UriSchemeHttps;
        return new UriBuilder(scheme, System.Net.IPAddress.Loopback.ToString(), localPort, pathAndQuery).Uri;
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Task.FromResult(new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = OperationId,
            CorrelationId = handle.CorrelationId,
            AuditId = handle.AuditId,
            ProposalId = handle.ProposalId,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            AuthorizationOutcome = handle.AuthorizationOutcome,
            PolicyDecision = handle.PolicyDecision,
            Status = handle.Status,
            Result = handle.Result,
            JobId = handle.JobId,
            ApprovalLane = handle.ApprovalLane,
            MetadataRevision = handle.MetadataRevision,
            Reason = handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        });
    }

    private static string BindPath(OperationRequest request, string path)
    {
        foreach (var name in RouteNames(path))
            path = path.Replace($"{{{name}}}", Uri.EscapeDataString(request.Parameters.GetValueOrDefault(name)
                ?? throw new ArgumentException($"Required route parameter '{name}' is missing.")), StringComparison.Ordinal);
        return path;
    }

    private string AppendQuery(string path, OperationRequest request)
    {
        if (_definition.Method != HttpMethod.Get) return path;
        var routeNames = RouteNames(_definition.Path);
        var query = request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}").ToArray();
        return query.Length == 0 ? path : $"{path}?{string.Join('&', query)}";
    }

    private static HashSet<string> RouteNames(string path) => path.Split('/')
        .Where(static segment => segment.StartsWith('{') && segment.EndsWith('}'))
        .Select(static segment => segment[1..^1]).ToHashSet(StringComparer.Ordinal);

    private static void CopyHeader(HttpContext current, HttpRequestMessage message, string name)
    {
        if (current.Request.Headers.TryGetValue(name, out var values) && values.Count > 0)
        {
            message.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }

    private static string SerializeBody(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var pair in parameters)
            {
                writer.WritePropertyName(pair.Key);
                try
                {
                    using var value = JsonDocument.Parse(pair.Value!);
                    value.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    writer.WriteStringValue(pair.Value);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
