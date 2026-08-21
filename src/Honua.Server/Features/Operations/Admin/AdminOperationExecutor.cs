// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Operations.Admin;

/// <summary>
/// Thin executor for one OpenAPI-bound admin operation. It invokes the already-registered
/// minimal-API endpoint delegate in the current request scope; it does not use loopback HTTP and
/// therefore reuses the endpoint's existing services, validation, errors, and transaction path.
/// </summary>
internal sealed class AdminOperationExecutor(
    AdminOpenApiOperationDefinition definition,
    AdminOpenApiOperationCatalog catalog,
    AdminEndpointOperationInvoker endpointInvoker) : IOperationExecutor
{
    public string OperationId => definition.Descriptor.OperationId;

    public Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new List<string>();
        foreach (var parameter in definition.Parameters.Where(parameter => parameter.Required))
        {
            if (!request.Parameters.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                messages.Add($"Required parameter '{parameter.Name}' is missing.");
            }
        }

        request.Parameters.TryGetValue("body", out var body);
        if (definition.HasRequestBody
            && definition.Descriptor.InputSchema.FirstOrDefault(parameter => parameter.Name == "body")?.Required == true
            && string.IsNullOrWhiteSpace(body))
        {
            messages.Add("Required parameter 'body' is missing.");
        }
        else if (!string.IsNullOrWhiteSpace(body)
                 && !AdminEndpointOperationInvoker.TryMeasurePayload(
                     definition,
                     body,
                     out _,
                     out var payloadError))
        {
            messages.Add(payloadError!);
        }

        if (request.DryRun && !definition.Descriptor.Policy.SupportsDryRun)
        {
            messages.Add($"Operation '{OperationId}' does not define a dry-run binding.");
        }

        return Task.FromResult(new OperationValidation
        {
            IsValid = messages.Count == 0,
            Status = messages.Count == 0 ? "valid" : "invalid",
            Messages = messages,
        });
    }

    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var validation = await ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return new OperationHandle
            {
                OperationId = OperationId,
                HandleId = NewHandleId(),
                Status = OperationHandleStatus.Failed,
                Reason = string.Join(" ", validation.Messages),
            };
        }

        var executionDefinition = request.DryRun
            ? catalog.GetRequired(definition.Descriptor.Policy.DryRunOperationId!)
            : definition;
        return await endpointInvoker
            .InvokeAsync(executionDefinition, request, context, OperationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<OperationStatus> GetStatusAsync(
        OperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Task.FromResult(new OperationStatus
        {
            OperationId = OperationId,
            HandleId = handle.HandleId,
            Status = handle.Status,
            Result = handle.Result,
            JobId = handle.JobId,
            MetadataRevision = handle.MetadataRevision,
        });
    }

    private static string NewHandleId() => $"op-{Guid.NewGuid():N}"[..32];
}

/// <summary>
/// Executes a catalog operation through its existing minimal-API endpoint delegate while
/// explicitly preserving authorization, tenant, idempotency, and audit context.
/// </summary>
internal sealed class AdminEndpointOperationInvoker(
    IEnumerable<EndpointDataSource> endpointDataSources,
    IAuthorizationPolicyProvider authorizationPolicyProvider,
    IPolicyEvaluator policyEvaluator,
    IServiceProvider services,
    TimeProvider clock,
    IOptions<LimitsOptions> limitsOptions)
{
    internal const int MaxCapturedResponseBytes = 64 * 1024;

    private readonly IReadOnlyList<EndpointDataSource> _endpointDataSources = endpointDataSources.ToArray();
    private readonly long _maxRequestBodyBytes = Math.Max(1, limitsOptions.Value.MaxUploadSizeBytes);

    public async Task<OperationHandle> InvokeAsync(
        AdminOpenApiOperationDefinition definition,
        OperationRequest operationRequest,
        OperationPolicyContext operationContext,
        string resultOperationId,
        CancellationToken cancellationToken)
    {
        operationRequest.Parameters.TryGetValue("body", out var requestBody);
        var payloadBytes = 0L;
        if (!string.IsNullOrWhiteSpace(requestBody)
            && !TryMeasurePayload(definition, requestBody, out payloadBytes, out var payloadError))
        {
            return Failure(resultOperationId, HttpStatusCode.BadRequest, payloadError!);
        }

        if (!string.IsNullOrWhiteSpace(requestBody) && payloadBytes > _maxRequestBodyBytes)
        {
            return Failure(
                resultOperationId,
                HttpStatusCode.RequestEntityTooLarge,
                $"The admin operation request body exceeds the configured {_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}-byte upload limit.");
        }

        var endpoint = ResolveEndpoint(definition);
        using var responseBody = new BoundedCaptureStream(MaxCapturedResponseBytes);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = BuildPrincipal(operationContext),
        };
        httpContext.SetEndpoint(endpoint);
        httpContext.RequestAborted = cancellationToken;
        httpContext.Response.Body = responseBody;
        httpContext.Request.Method = definition.Method;
        httpContext.Request.RouteValues["version"] = "1";
        httpContext.Request.Path = BindPath(definition, operationRequest, httpContext.Request.RouteValues);
        BindQueryAndHeaders(definition, operationRequest, httpContext.Request);
        if (!string.IsNullOrWhiteSpace(operationRequest.IdempotencyKey))
        {
            httpContext.Request.Headers["Idempotency-Key"] = operationRequest.IdempotencyKey;
        }

        await BindBodyAsync(definition, operationRequest, httpContext.Request, cancellationToken).ConfigureAwait(false);

        var ambientAccessor = services.GetService<IHttpContextAccessor>();
        var previousContext = ambientAccessor?.HttpContext;
        if (ambientAccessor is not null)
        {
            ambientAccessor.HttpContext = httpContext;
        }

        try
        {
            if (!TenantMatches(operationContext))
            {
                return Failure(resultOperationId, HttpStatusCode.Forbidden, "The operation tenant does not match the active request tenant.");
            }

            SetTenantContext(operationContext);
            if (!await IsTenantActiveAsync(operationContext, cancellationToken).ConfigureAwait(false))
            {
                return Failure(resultOperationId, HttpStatusCode.Forbidden, "Tenant access is unavailable.");
            }

            var authorization = await AuthorizeAsync(endpoint, httpContext).ConfigureAwait(false);
            if (!authorization)
            {
                await WriteAuditAsync(definition, operationContext, AuditOutcome.Denied, cancellationToken)
                    .ConfigureAwait(false);
                return Failure(resultOperationId, HttpStatusCode.Forbidden, "The invoking principal is not authorized for this admin operation.");
            }

            await endpoint.RequestDelegate!(httpContext).ConfigureAwait(false);
            responseBody.Position = 0;
            using var responseReader = new StreamReader(responseBody, Encoding.UTF8, leaveOpen: true);
            var responseText = await responseReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var success = httpContext.Response.StatusCode is >= 200 and < 300;
            await WriteAuditAsync(
                    definition,
                    operationContext,
                    success ? AuditOutcome.Success : AuditOutcome.Failure,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!success)
            {
                return Failure(
                    resultOperationId,
                    (HttpStatusCode)httpContext.Response.StatusCode,
                    $"Admin operation failed with HTTP status {httpContext.Response.StatusCode.ToString(CultureInfo.InvariantCulture)}.");
            }

            var jobId = TryReadIdentifier(responseText, "jobId", "operationId");
            var safeResponseText = AdminOperationResponseRedactor.Redact(responseText);
            var status = httpContext.Response.StatusCode == StatusCodes.Status202Accepted
                ? OperationHandleStatus.Queued
                : OperationHandleStatus.Completed;
            return new OperationHandle
            {
                OperationId = resultOperationId,
                HandleId = NewHandleId(),
                Status = status,
                JobId = status == OperationHandleStatus.Queued ? jobId : null,
                Result = new OperationResultSummary
                {
                    Summary = operationRequest.DryRun
                        ? $"Dry run completed through '{definition.Descriptor.OperationId}'."
                        : $"Admin operation '{resultOperationId}' completed.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["httpStatus"] = httpContext.Response.StatusCode.ToString(CultureInfo.InvariantCulture),
                        ["response"] = safeResponseText,
                        ["responseTruncated"] = responseBody.WasTruncated.ToString(CultureInfo.InvariantCulture),
                    },
                },
            };
        }
        finally
        {
            if (ambientAccessor is not null)
            {
                ambientAccessor.HttpContext = previousContext;
            }

            if (!ReferenceEquals(httpContext.Request.Body, Stream.Null))
            {
                await httpContext.Request.Body.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private RouteEndpoint ResolveEndpoint(AdminOpenApiOperationDefinition definition)
    {
        foreach (var endpoint in _endpointDataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null
                || !methods.Contains(definition.Method, StringComparer.OrdinalIgnoreCase)
                || !RouteTemplatesMatch(endpoint.RoutePattern.RawText, definition.Path))
            {
                continue;
            }

            return endpoint;
        }

        throw new InvalidOperationException(
            $"No live endpoint matches {definition.Method} {definition.Path} for '{definition.Descriptor.OperationId}'.");
    }

    private async Task<bool> AuthorizeAsync(RouteEndpoint endpoint, HttpContext context)
    {
        var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        if (authorizeData.Count == 0)
        {
            return true;
        }

        var policy = await AuthorizationPolicy.CombineAsync(authorizationPolicyProvider, authorizeData)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return true;
        }

        var authenticationScheme = context.User.Identity?.AuthenticationType;
        if (string.IsNullOrWhiteSpace(authenticationScheme))
        {
            return false;
        }

        var ticket = new AuthenticationTicket(context.User, authenticationScheme);
        var authenticateResult = AuthenticateResult.Success(ticket);
        var result = await policyEvaluator.AuthorizeAsync(policy, authenticateResult, context, endpoint)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    private bool TenantMatches(OperationPolicyContext operationContext)
    {
        if (string.IsNullOrWhiteSpace(operationContext.TenantId))
        {
            return true;
        }

        var activeTenant = services.GetService<ITenantContext>()?.TenantId;
        return string.IsNullOrWhiteSpace(activeTenant)
            || string.Equals(activeTenant, operationContext.TenantId, StringComparison.Ordinal);
    }

    private void SetTenantContext(OperationPolicyContext operationContext)
    {
        if (services.GetService<Honua.Infrastructure.MultiTenancy.RequestTenantContext>() is { } tenantContext)
        {
            tenantContext.Set(
                operationContext.TenantId,
                string.IsNullOrWhiteSpace(operationContext.TenantId)
                    ? TenantContextSource.Anonymous
                    : TenantContextSource.Claim);
        }
    }

    internal async Task<bool> IsTenantActiveAsync(
        OperationPolicyContext operationContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationContext.TenantId))
        {
            return true;
        }

        if (services.GetService<ITenantCatalog>() is not { } catalog)
        {
            return false;
        }

        var tenant = await catalog.GetAsync(operationContext.TenantId, cancellationToken).ConfigureAwait(false);
        return tenant is not null && tenant.Status == TenantStatus.Active;
    }

    private async Task WriteAuditAsync(
        AdminOpenApiOperationDefinition definition,
        OperationPolicyContext context,
        AuditOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (services.GetService<IAuditLog>() is not { } auditLog)
        {
            return;
        }

        await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = clock.GetUtcNow(),
            EventType = AuditEventType.AdminAction,
            Actor = context.PrincipalId ?? AuditEvent.AnonymousActor,
            ActorType = string.IsNullOrWhiteSpace(context.PrincipalId)
                ? AuditActorType.Anonymous
                : AuditActorType.UserId,
            ResourceType = "admin-operation",
            ResourceId = definition.Descriptor.OperationId,
            Action = definition.Descriptor.OperationId,
            Outcome = outcome,
            CorrelationId = context.CorrelationId ?? string.Empty,
            Details = $"{{\"openApiOperationId\":\"{definition.OpenApiOperationId}\"}}",
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ClaimsPrincipal BuildPrincipal(OperationPolicyContext context)
    {
        if (!CanonicalSecurityActor.IsCanonicalIdentity(
                context.PrincipalId,
                context.AuthenticationScheme,
                context.SubjectId,
                context.SubjectIssuer,
                context.ApiKeyId,
                context.CredentialKind))
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var claims = new List<Claim>();
        claims.Add(new Claim(ClaimTypes.Name, context.PrincipalId!));

        string authenticationType;
        if (Guid.TryParse(context.ApiKeyId, out var apiKeyId))
        {
            authenticationType = FrameworkAuthenticationIdentity.ApiKeyAuthenticationType;
            claims.Add(new Claim("api_key_id", apiKeyId.ToString("D")));
            claims.Add(new Claim(
                FrameworkAuthenticationIdentity.CredentialKindClaimType,
                FrameworkAuthenticationIdentity.ApiKeyCredentialKind));
            claims.Add(new Claim("auth_type", context.AuthenticationScheme!));
        }
        else
        {
            authenticationType = context.AuthenticationScheme switch
            {
                "client-certificate" => FrameworkAuthenticationIdentity.ClientCertificateAuthenticationType,
                "portal-token" => FrameworkAuthenticationIdentity.PortalTokenAuthenticationType,
                "scoped-job-token" => FrameworkAuthenticationIdentity.ScopedJobTokenAuthenticationType,
                _ => "PublishedOperation",
            };
            claims.Add(new Claim(ClaimTypes.NameIdentifier, context.SubjectId!));
            claims.Add(new Claim("sub", context.SubjectId!));
            if (IdentityProtocolProvenance.IsSupported(context.AuthenticationScheme))
            {
                claims.Add(new Claim(
                    IdentityProtocolProvenance.ClaimType,
                    context.AuthenticationScheme!));
            }

            if (string.Equals(
                    context.AuthenticationScheme,
                    IdentityProtocolProvenance.Oidc,
                    StringComparison.Ordinal))
            {
                claims.Add(new Claim("iss", context.SubjectIssuer!));
            }
        }

        claims.AddRange(context.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(context.Permissions.Select(permission => new Claim("permission", permission)));
        if (!string.IsNullOrWhiteSpace(context.TenantId))
        {
            claims.Add(new Claim("tenant_id", context.TenantId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    private static string BindPath(
        AdminOpenApiOperationDefinition definition,
        OperationRequest operationRequest,
        RouteValueDictionary routeValues)
    {
        var path = definition.Path;
        foreach (var parameter in definition.Parameters.Where(parameter => parameter.Location == "path"))
        {
            operationRequest.Parameters.TryGetValue(parameter.Name, out var value);
            value ??= string.Empty;
            path = path.Replace("{" + parameter.WireName + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
            routeValues[parameter.WireName] = value;
        }

        return path;
    }

    private static void BindQueryAndHeaders(
        AdminOpenApiOperationDefinition definition,
        OperationRequest operationRequest,
        HttpRequest request)
    {
        var query = new List<KeyValuePair<string, string?>>();
        foreach (var parameter in definition.Parameters)
        {
            if (!operationRequest.Parameters.TryGetValue(parameter.Name, out var value) || value is null)
            {
                continue;
            }

            if (parameter.Location == "query")
            {
                query.Add(new KeyValuePair<string, string?>(parameter.WireName, value));
            }
            else if (parameter.Location == "header")
            {
                request.Headers[parameter.WireName] = value;
            }
        }

        request.QueryString = QueryString.Create(query);
    }

    private static async Task BindBodyAsync(
        AdminOpenApiOperationDefinition definition,
        OperationRequest operationRequest,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!definition.HasRequestBody
            || !operationRequest.Parameters.TryGetValue("body", out var body)
            || string.IsNullOrWhiteSpace(body))
        {
            request.Body = Stream.Null;
            return;
        }

        if (string.Equals(definition.RequestContentType, "multipart/form-data", StringComparison.Ordinal))
        {
            using var content = BuildMultipart(definition, body);
            var stream = new MemoryStream();
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            request.Body = stream;
            request.ContentLength = stream.Length;
            request.ContentType = content.Headers.ContentType?.ToString();
            return;
        }

        if (string.Equals(definition.RequestContentType, "application/octet-stream", StringComparison.Ordinal)
            && IsBinarySchema(definition.RequestBodyJsonSchema))
        {
            var decodedBytes = Convert.FromBase64String(body);
            request.Body = new MemoryStream(decodedBytes, writable: false);
            request.ContentLength = decodedBytes.Length;
            request.ContentType = definition.RequestContentType;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        request.Body = new MemoryStream(bytes, writable: false);
        request.ContentLength = bytes.Length;
        request.ContentType = definition.RequestContentType ?? "application/json";
    }

    private static MultipartFormDataContent BuildMultipart(
        AdminOpenApiOperationDefinition definition,
        string body)
    {
        var content = new MultipartFormDataContent();
        using var document = JsonDocument.Parse(body);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (property.Name.EndsWith("FileName", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsMultipartBinaryProperty(definition.RequestBodyJsonSchema, property.Name)
                && property.Value.ValueKind == JsonValueKind.String
                && TryDecodeBase64(property.Value.GetString(), out var bytes))
            {
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var fileNameProperty = property.Name + "FileName";
                var fileName = document.RootElement.TryGetProperty(fileNameProperty, out var fileNameValue)
                    ? fileNameValue.GetString()
                    : property.Name;
                content.Add(fileContent, property.Name, fileName ?? property.Name);
            }
            else
            {
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                content.Add(new StringContent(value, Encoding.UTF8), property.Name);
            }
        }

        return content;
    }

    private static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value ?? string.Empty);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    internal static bool TryMeasurePayload(
        AdminOpenApiOperationDefinition definition,
        string body,
        out long payloadBytes,
        out string? error)
    {
        payloadBytes = 0;
        error = null;

        if (string.Equals(definition.RequestContentType, "application/json", StringComparison.Ordinal))
        {
            try
            {
                using var _ = JsonDocument.Parse(body);
                payloadBytes = Encoding.UTF8.GetByteCount(body);
                return true;
            }
            catch (JsonException)
            {
                error = "Parameter 'body' must contain valid JSON.";
                return false;
            }
        }

        if (string.Equals(definition.RequestContentType, "application/octet-stream", StringComparison.Ordinal)
            && IsBinarySchema(definition.RequestBodyJsonSchema))
        {
            if (!TryDecodeBase64(body, out var bytes))
            {
                error = "Parameter 'body' must contain valid base64-encoded binary data.";
                return false;
            }

            payloadBytes = bytes.LongLength;
            return true;
        }

        if (string.Equals(definition.RequestContentType, "multipart/form-data", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "Parameter 'body' must contain a JSON object for multipart form data.";
                    return false;
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.EndsWith("FileName", StringComparison.Ordinal))
                    {
                        payloadBytes += Encoding.UTF8.GetByteCount(property.Value.GetString() ?? string.Empty);
                        continue;
                    }

                    if (IsMultipartBinaryProperty(definition.RequestBodyJsonSchema, property.Name))
                    {
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !TryDecodeBase64(property.Value.GetString(), out var bytes))
                        {
                            error = $"Multipart binary property '{property.Name}' must contain valid base64 data.";
                            return false;
                        }

                        payloadBytes += bytes.LongLength;
                    }
                    else
                    {
                        var value = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString() ?? string.Empty
                            : property.Value.GetRawText();
                        payloadBytes += Encoding.UTF8.GetByteCount(value);
                    }
                }

                return true;
            }
            catch (JsonException)
            {
                error = "Parameter 'body' must contain valid JSON for multipart form data.";
                return false;
            }
        }

        payloadBytes = Encoding.UTF8.GetByteCount(body);
        return true;
    }

    private static bool IsBinarySchema(JsonElement? schema)
        => schema is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty("type", out var type)
           && string.Equals(type.GetString(), "string", StringComparison.Ordinal)
           && value.TryGetProperty("format", out var format)
           && string.Equals(format.GetString(), "binary", StringComparison.Ordinal);

    private static bool IsMultipartBinaryProperty(JsonElement? schema, string propertyName)
        => schema is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty("properties", out var properties)
           && properties.ValueKind == JsonValueKind.Object
           && properties.TryGetProperty(propertyName, out var propertySchema)
           && IsBinarySchema(propertySchema);

    private static bool RouteTemplatesMatch(string? registered, string expected)
        => string.Equals(NormalizeRouteTemplate(registered), NormalizeRouteTemplate(expected), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRouteTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var result = new StringBuilder(template.Length);
        var insideParameter = false;
        var copiedParameterName = false;
        foreach (var value in template)
        {
            if (value == '{')
            {
                insideParameter = true;
                copiedParameterName = false;
                result.Append(value);
            }
            else if (insideParameter && value == '}')
            {
                insideParameter = false;
                result.Append(value);
            }
            else if (insideParameter && (value == ':' || value == '=' || value == '?'))
            {
                copiedParameterName = true;
            }
            else if (!insideParameter || !copiedParameterName)
            {
                result.Append(value);
            }
        }

        return result.ToString()
            .Replace("{version}", "1", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
    }

    private static string? TryReadIdentifier(string response, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            return FindIdentifier(document.RootElement, names);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindIdentifier(JsonElement element, IReadOnlyList<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText();
                }

                var nested = FindIdentifier(property.Value, names);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindIdentifier(item, names);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static OperationHandle Failure(string operationId, HttpStatusCode status, string reason)
        => new()
        {
            OperationId = operationId,
            HandleId = NewHandleId(),
            Status = OperationHandleStatus.Failed,
            Reason = reason,
            Result = new OperationResultSummary
            {
                Summary = "Admin operation did not complete.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["httpStatus"] = ((int)status).ToString(CultureInfo.InvariantCulture),
                },
            },
        };

    private static string NewHandleId() => $"op-{Guid.NewGuid():N}"[..32];
}

/// <summary>
/// Captures a bounded prefix of an in-process endpoint response and discards the remainder.
/// This preserves useful MCP result detail without buffering an unbounded export/list response.
/// </summary>
internal sealed class BoundedCaptureStream(int maximumBytes) : Stream
{
    private readonly MemoryStream _inner = new(Math.Min(maximumBytes, 4096));

    public bool WasTruncated { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(Math.Min(value, maximumBytes));

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var remaining = maximumBytes - checked((int)_inner.Length);
        var writeLength = Math.Min(buffer.Length, Math.Max(0, remaining));
        if (writeLength > 0)
        {
            _inner.Write(buffer[..writeLength]);
        }

        WasTruncated |= writeLength < buffer.Length;
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
