// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
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
        var messages = new List<string>();
        foreach (var parameter in _descriptor.InputSchema)
        {
            var value = request.Parameters.GetValueOrDefault(parameter.Name);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (parameter.Required)
                {
                    messages.Add(routeNames.Contains(parameter.Name)
                        ? $"Required route parameter '{parameter.Name}' is missing."
                        : $"Required parameter '{parameter.Name}' is missing.");
                }
                continue;
            }

            var schema = AdminOperateOperationCatalog.GetInputContract(_definition, parameter.Name);
            if (parameter.Schema.Type == WorkflowSchemaValueType.Text)
            {
                ValidateText(value, schema, parameter.Name, messages);
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(value);
                ValidateValue(document.RootElement, schema, parameter.Name, messages);
            }
            catch (JsonException)
            {
                messages.Add($"Parameter '{parameter.Name}' must contain valid JSON.");
            }
        }

        if (OperationId == "admin.metadata.prevalidate")
        {
            var packageId = request.Parameters.GetValueOrDefault("releasePackageId");
            var inlinePackage = request.Parameters.GetValueOrDefault("releasePackage");
            var hasId = !string.IsNullOrWhiteSpace(packageId);
            var hasInline = !string.IsNullOrWhiteSpace(inlinePackage) && inlinePackage.Trim() != "null";
            if (hasId == hasInline)
                messages.Add("Exactly one of 'releasePackageId' or 'releasePackage' is required.");
            if (hasId && (!Guid.TryParse(packageId, out var id) || id == Guid.Empty))
                messages.Add("Parameter 'releasePackageId' must be a non-empty UUID.");
        }

        if (OperationId == "admin.cache.invalidate")
        {
            var scope = request.Parameters.GetValueOrDefault("scope")?.ToLowerInvariant();
            string[] required = scope switch
            {
                "layer" => ["serviceId", "layerId"],
                "service" => ["serviceId"],
                "collection" => ["collectionId"],
                _ => [],
            };
            foreach (var name in required.Where(name => string.IsNullOrWhiteSpace(request.Parameters.GetValueOrDefault(name)) ||
                (name == "layerId" && request.Parameters.GetValueOrDefault(name)?.Trim() == "null")))
                messages.Add($"Required parameter '{name}' is missing for '{scope}' scope.");
        }

        return Task.FromResult(new OperationValidation
        {
            IsValid = messages.Count == 0,
            Status = messages.Count == 0 ? "valid" : "invalid",
            Messages = messages.ToArray()
        });
    }

    private static void ValidateValue(JsonElement value, JsonElement schema, string path, List<string> messages)
    {
        schema = AdminOperateOperationCatalog.ResolveInputContract(schema);
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!schema.TryGetProperty("nullable", out var nullable) || !nullable.GetBoolean())
                messages.Add($"Parameter '{path}' cannot be null.");
            return;
        }
        var type = schema.TryGetProperty("type", out var declaredType) ? declaredType.GetString() : "object";
        var valid = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
        if (!valid)
        {
            messages.Add($"Parameter '{path}' has an invalid JSON type.");
            return;
        }
        if (value.ValueKind == JsonValueKind.String)
            ValidateText(value.GetString()!, schema, path, messages);
        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("format", out var format) &&
            format.GetString() == "int32" && !value.TryGetInt32(out _))
            messages.Add($"Parameter '{path}' must be a 32-bit integer.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(static item => item.GetString()!)
                    .Where(name => !value.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.Null ||
                        (member.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(member.GetString()))))
                {
                    messages.Add($"Required parameter '{path}.{name}' is missing.");
                }
            }
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out var child))
                        ValidateValue(property.Value, child, $"{path}.{property.Name}", messages);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateValue(item, items, $"{path}[{index++}]", messages);
        }
    }

    private static void ValidateText(string value, JsonElement schema, string path, List<string> messages)
    {
        if (schema.TryGetProperty("enum", out var values) &&
            !values.EnumerateArray().Any(item => item.GetString() == value))
            messages.Add($"Parameter '{path}' is not an allowed value.");
        if (!schema.TryGetProperty("format", out var format)) return;
        if (format.GetString() == "uuid" && !Guid.TryParse(value, out _))
            messages.Add($"Parameter '{path}' must be a UUID.");
        if (format.GetString() == "date-time")
        {
            using var encoded = JsonDocument.Parse("\"" + JsonEncodedText.Encode(value) + "\"");
            if (!encoded.RootElement.TryGetDateTimeOffset(out _))
                messages.Add($"Parameter '{path}' must be an ISO 8601 date-time.");
        }
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
        var uri = BuildLocalUri(current, $"/api/v1/admin{path}", BuildQuery(request));
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
                AdminApiKeyPermission.CreateApprovedOperationGrants(method.Method, uri.AbsolutePath, context.TenantId),
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

    private static Uri BuildLocalUri(HttpContext current, string path, string query)
    {
        var localPort = current.Connection.LocalPort;
        if (localPort <= 0)
            throw new InvalidOperationException("Admin operation loopback requires the local server port.");

        var scheme = current.Features.Get<Microsoft.AspNetCore.Http.Features.ITlsConnectionFeature>() is null
            ? Uri.UriSchemeHttp
            : Uri.UriSchemeHttps;
        return new UriBuilder(scheme, System.Net.IPAddress.Loopback.ToString(), localPort, path)
        {
            Query = query
        }.Uri;
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

    private string BuildQuery(OperationRequest request)
    {
        if (_definition.Method != HttpMethod.Get) return string.Empty;
        var routeNames = RouteNames(_definition.Path);
        var query = request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}").ToArray();
        return string.Join('&', query);
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

    private string SerializeBody(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var schema = _descriptor.InputSchema;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var pair in parameters)
            {
                writer.WritePropertyName(pair.Key);
                var parameter = schema.FirstOrDefault(input => input.Name == pair.Key);
                if (parameter?.Schema.Type == WorkflowSchemaValueType.Text)
                {
                    writer.WriteStringValue(pair.Value);
                    continue;
                }
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
