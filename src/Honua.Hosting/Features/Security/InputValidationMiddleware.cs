// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security;

internal sealed class InputValidationMiddleware
{
    private const long PreAuthMaxFormBodyBytes = 1_048_576;

    private static readonly FormOptions _preAuthFormOptions = new()
    {
        ValueLengthLimit = (int)PreAuthMaxFormBodyBytes,
        MultipartBodyLengthLimit = PreAuthMaxFormBodyBytes,
        BufferBodyLengthLimit = PreAuthMaxFormBodyBytes,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<InputValidationMiddleware> _logger;
    private readonly InputValidationOptions _options;
    private readonly GeometryLimits _geometryLimits;
    private readonly long _maxProtocolPayloadLength;

    // Common injection patterns to detect and block. They run before authentication on values up to
    // the geometry and edit payload limits, so they use the linear-time engine: only IsMatch is
    // called, and whether a match exists does not depend on the engine.
    private static readonly Regex _sqlInjectionPattern = new(
        @"(\bunion\s+(?:all\s+)?select\b)|(\bselect\b.+\bfrom\b)|(\binsert\s+into\b)" +
        @"|(\bupdate\s+[\w\.\[\]""`]+\s+set\b)|(\bdelete\s+from\b)" +
        @"|(\bdrop\s+(?:table|database|schema|view|index)\b)" +
        @"|(\b(?:create|alter)\s+(?:table|database|schema|view|index|procedure|function)\b)" +
        @"|(\bexec(?:ute)?\s+[\w\.\[\]""`]+)" +
        @"|(\-\-|\/\*|\*\/)|(\bor\b\s+\d+\s*=\s*\d+)" +
        @"|(\bor\b\s+['""][^'""]*['""]\s*=\s*['""][^'""]*['""])" +
        @"|(;\s*(?:select|insert|update|delete|drop|create|alter|exec|execute)\b)",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex _xssPattern = new(
        @"<script[^>]*>.*?</script>|<iframe[^>]*>.*?</iframe>|javascript:|vbscript:|onload\s*=|onclick\s*=|onerror\s*=" +
        @"|eval\s*\(|expression\s*\(|<object[^>]*>|<embed[^>]*>|<applet[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.Singleline);

    private static readonly Regex _commandInjectionPattern = new(
        @"(\|\s*(cat|ls|rm|mv|cp|chmod|chown|wget|curl|bash|sh|zsh|cmd|powershell|pwsh|python|perl|php|node|nc|netcat)\b)" +
        @"|(;\s*(cat|ls|rm|mv|cp|chmod|chown|wget|curl|bash|sh|zsh|cmd|powershell|pwsh|python|perl|php|node|nc|netcat)\b)" +
        @"|(&&\s*(cat|ls|rm|mv|cp|chmod|chown|wget|curl|bash|sh|zsh|cmd|powershell|pwsh|python|perl|php|node|nc|netcat)\b)" +
        @"|(\$\(.*\))|(cmd\s+/c)|(powershell)|(\beval\b)|(\bexec\b)|(\bsystem\b)" +
        @"|(\bshell_exec\b)|(\bpassthru\b)|(\bpopen\b)|(\bproc_open\b)",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex _pathTraversalPattern = new(
        @"(\.{2}[/\\])|(\.\.[/\\])|([/\\]\.\.[/\\])|(\.\.[/\\]\.\.)|(\.{3,})|(\%2e\%2e)|(\%252e\%252e)" +
        @"|(\.%2f)|(\.\%5c)|(\.%252f)|(\.%255c)",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    // Suspicious header patterns
    private static readonly FrozenSet<string> _dangerousHeaders = new[]
    {
        "X-Forwarded-For", // Can be spoofed
        "X-Real-IP",      // Can be spoofed
        "X-Originating-IP",
        "CF-Connecting-IP",
        "True-Client-IP",
        "X-Cluster-Client-IP"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _securitySensitiveHeaders = new[]
    {
        "X-API-Key",
        "Authorization",
        "X-Correlation-ID",
        "X-Forwarded-Proto",
        "X-Forwarded-Host"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _opaqueCredentialHeaders = new[]
    {
        "Authorization",
        "X-API-Key"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public InputValidationMiddleware(
        RequestDelegate next,
        ILogger<InputValidationMiddleware> logger,
        IOptions<InputValidationOptions> options,
        IOptions<LimitsOptions> limits)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
        _geometryLimits = limits.Value.Geometry;
        var maxPayloadSize = limits.Value.Edits.MaxPayloadSize;
        _maxProtocolPayloadLength = maxPayloadSize > 0 ? maxPayloadSize : 26_214_400;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Skip validation for specific paths if configured
        if (ShouldSkipValidation(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Parse the form asynchronously up front so the synchronous HttpRequest.Form
        // access in ValidateRequest reuses the cached result instead of blocking a
        // threadpool thread on sync-over-async body I/O (FormFeature.ReadForm).
        // An unmatched route has no handler that needs form fields. Parsing it
        // would buffer an anonymous body before authentication.
        var endpoint = context.GetEndpoint();
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var formReadOutcome = await TryReadFormAsync(context, endpoint, cancellationToken);
        if (formReadOutcome == FormReadOutcome.InvalidFormData)
        {
            InputValidationLog.InputRejected(_logger, "Invalid form data.");
            await CreateSecurityErrorResponse(context, "Invalid form data.");
            return;
        }

        // Validate request inputs
        var geometryMetadata = endpoint?.Metadata.GetMetadata<GeometryParameterMetadata>();
        var validationResult = endpoint is not null
            ? ValidateMatchedEndpointRequest(context.Request, geometryMetadata)
            : ValidateUnmatchedRequest(context.Request);
        if (validationResult.IsValid && geometryMetadata != null)
        {
            var body = await geometryMetadata.ReadBodyParametersAsync(context.Request, cancellationToken);
            if (body.Error != null)
            {
                validationResult = InputValidationResult.Invalid(body.Error, isSuspicious: false);
            }
            else if (body.Values != null)
            {
                foreach (var parameter in body.Values)
                {
                    // The protocol parser uses the same conversions for JSON and form
                    // values. Apply the existing form-parameter security semantics to both.
                    var inspection = new ParameterInspectionContext(context.Request, "form", parameter.Key);
                    validationResult = ValidateParameter(inspection, parameter.Value, geometryMetadata);
                    if (!validationResult.IsValid)
                    {
                        break;
                    }
                }
            }
        }
        if (!validationResult.IsValid)
        {
            if (validationResult.IsSuspicious)
            {
                InputValidationLog.MaliciousInputDetected(_logger,
                    validationResult.ErrorMessage,
                    context.Connection.RemoteIpAddress?.ToString());
            }
            else
            {
                InputValidationLog.InputRejected(_logger, validationResult.ErrorMessage);
            }

            await CreateSecurityErrorResponse(context, validationResult.ErrorMessage ?? "Input validation failed");
            return;
        }

        await _next(context);
    }

    private bool ShouldSkipValidation(PathString path)
    {
        var pathValue = path.Value;
        if (string.IsNullOrEmpty(pathValue))
        {
            return false;
        }

        var pathSpan = pathValue.AsSpan();
        if (pathSpan.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            pathSpan.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase) ||
            pathSpan.StartsWith("/ready", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var excludedPaths = _options.ExcludedPaths;
        for (var i = 0; i < excludedPaths.Length; i++)
        {
            var excluded = excludedPaths[i];
            if (!string.IsNullOrEmpty(excluded) && pathSpan.StartsWith(excluded.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private InputValidationResult ValidateUnmatchedRequest(HttpRequest request)
    {
        var queryResult = ValidateQueryParameters(request, geometryMetadata: null);
        return queryResult.IsValid ? ValidateRequestHeaders(request) : queryResult;
    }

    private InputValidationResult ValidateMatchedEndpointRequest(
        HttpRequest request,
        GeometryParameterMetadata? geometryMetadata)
    {
        var queryResult = ValidateQueryParameters(request, geometryMetadata);
        if (!queryResult.IsValid)
        {
            return queryResult;
        }

        var formResult = ValidateFormParameters(request, geometryMetadata);
        return formResult.IsValid ? ValidateRequestHeaders(request) : formResult;
    }

    private InputValidationResult ValidateQueryParameters(
        HttpRequest request,
        GeometryParameterMetadata? geometryMetadata)
    {
        foreach (var param in request.Query)
        {
            if (string.IsNullOrEmpty(param.Key))
            {
                continue;
            }

            var result = ValidateParameter(new ParameterInspectionContext(request, "query", param.Key), param.Value, geometryMetadata);
            if (!result.IsValid)
            {
                return result;
            }
        }

        return InputValidationResult.Valid();
    }

    private enum FormReadOutcome
    {
        Ready,
        Skipped,
        InvalidFormData,
    }

    private static async Task<FormReadOutcome> TryReadFormAsync(
        HttpContext context,
        Endpoint? endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint is null
            || !context.Request.HasFormContentType
            || InputValidationRules.IsStreamingMultipartImportPath(context.Request.Path.Value))
        {
            return FormReadOutcome.Skipped;
        }

        if (ShouldSkipPreAuthFormRead(context, endpoint))
        {
            return FormReadOutcome.Skipped;
        }

        try
        {
            await ReadBoundedFormAsync(context, endpoint, cancellationToken);
            return FormReadOutcome.Ready;
        }
        catch (InvalidDataException)
        {
            return FormReadOutcome.InvalidFormData;
        }
    }

    private static bool ShouldSkipPreAuthFormRead(HttpContext context, Endpoint endpoint)
        => RequiresPreAuthFormCap(context, endpoint)
           && (!context.Request.ContentLength.HasValue
               || context.Request.ContentLength.Value > PreAuthMaxFormBodyBytes);

    private static bool RequiresPreAuthFormCap(HttpContext context, Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
           && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
           && !LimitsEnforcementMiddleware.HasCallerCredential(context);

    private static async Task ReadBoundedFormAsync(
        HttpContext context,
        Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (RequiresPreAuthFormCap(context, endpoint))
        {
            var formFeature = new FormFeature(context.Request, _preAuthFormOptions);
            await formFeature.ReadFormAsync(cancellationToken);
            context.Features.Set<IFormFeature>(formFeature);
            return;
        }

        await context.Request.ReadFormAsync(cancellationToken);
    }

    private InputValidationResult ValidateFormParameters(
        HttpRequest request,
        GeometryParameterMetadata? geometryMetadata)
    {
        if (!request.HasFormContentType
            || InputValidationRules.IsStreamingMultipartImportPath(request.Path.Value)
            || (HttpMethods.IsPost(request.Method) && geometryMetadata != null)
            || request.HttpContext.Features.Get<IFormFeature>()?.Form is not { } form)
        {
            return InputValidationResult.Valid();
        }

        foreach (var param in form)
        {
            if (string.IsNullOrEmpty(param.Key))
            {
                continue;
            }

            var result = ValidateParameter(new ParameterInspectionContext(request, "form", param.Key), param.Value, geometryMetadata);
            if (!result.IsValid)
            {
                return result;
            }
        }

        return InputValidationResult.Valid();
    }

    private InputValidationResult ValidateRequestHeaders(HttpRequest request)
    {
        foreach (var header in request.Headers)
        {
            if (string.IsNullOrEmpty(header.Key))
            {
                continue;
            }

            if (_dangerousHeaders.Contains(header.Key) && _options.ValidateSuspiciousHeaders)
            {
                var result = ValidateHeaderValue(header.Key, header.Value);
                if (!result.IsValid)
                {
                    return result;
                }
            }

            if (IsSecuritySensitiveHeader(header.Key))
            {
                var result = IsOpaqueCredentialHeader(header.Key)
                    ? ValidateOpaqueCredentialHeaderValue(header.Key, header.Value)
                    : ValidateParameter(new ParameterInspectionContext(request, "header", header.Key), header.Value, geometryMetadata: null);
                if (!result.IsValid)
                {
                    return result;
                }
            }
        }

        return InputValidationResult.Valid();
    }

    private InputValidationResult ValidateOpaqueCredentialHeaderValue(
        string headerName,
        Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (value.Length > _options.MaxParameterLength)
            {
                return InputValidationResult.Invalid($"Header '{headerName}' exceeds maximum length of {_options.MaxParameterLength}");
            }

            if (value.AsSpan().ContainsAny('\r', '\n'))
            {
                return InputValidationResult.Invalid($"Header injection attempt detected in '{headerName}'");
            }

            if (_options.DetectNullBytes && value.Contains('\0'))
            {
                return InputValidationResult.Invalid($"Null byte detected in header parameter '{headerName}'");
            }

            if (_options.DetectControlCharacters && InputValidationRules.ContainsControlCharacters(value))
            {
                return InputValidationResult.Invalid($"Control characters detected in header parameter '{headerName}'");
            }
        }

        return InputValidationResult.Valid();
    }

    private InputValidationResult ValidateParameter(
        ParameterInspectionContext parameter,
        Microsoft.Extensions.Primitives.StringValues values,
        GeometryParameterMetadata? geometryMetadata)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var lengthResult = ValidateParameterLength(parameter, value, geometryMetadata);
            if (!lengthResult.IsValid)
            {
                return lengthResult;
            }

            var threatResult = ValidateParameterThreats(parameter, value);
            if (!threatResult.IsValid)
            {
                return threatResult;
            }
        }

        return InputValidationResult.Valid();
    }

    private InputValidationResult ValidateParameterLength(
        ParameterInspectionContext parameter,
        string value,
        GeometryParameterMetadata? geometryMetadata)
    {
        if (geometryMetadata != null && parameter.Name.Equals(geometryMetadata.ParameterName, StringComparison.OrdinalIgnoreCase))
        {
            if (CheckGeometrySize(parameter.Name, value) is { } sizeError)
            {
                return sizeError;
            }

            parameter.Request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var geometryError = geometryMetadata.Validate(value, _geometryLimits.MaxVerticesPerGeometry, parameter.Request.HttpContext.RequestAborted);
            return geometryError != null
                ? InputValidationResult.Invalid(geometryError, isSuspicious: false)
                : InputValidationResult.Valid();
        }

        if (InputValidationRules.IsGeometryServiceGeometryField(parameter))
        {
            return CheckGeometrySize(parameter.Name, value) ?? InputValidationResult.Valid();
        }

        if (InputValidationRules.IsGeoServicesProtocolPayloadField(parameter))
        {
            return value.Length > _maxProtocolPayloadLength
                ? InputValidationResult.Invalid(
                    $"Parameter '{parameter.Name}' exceeds maximum length of {_maxProtocolPayloadLength.ToString(CultureInfo.InvariantCulture)}",
                    isSuspicious: false)
                : InputValidationResult.Valid();
        }

        return value.Length > _options.MaxParameterLength
            ? InputValidationResult.Invalid($"Parameter '{parameter.Name}' exceeds maximum length of {_options.MaxParameterLength}", isSuspicious: false)
            : InputValidationResult.Valid();
    }

    private InputValidationResult ValidateParameterThreats(ParameterInspectionContext parameter, string value)
    {
        var sqlValidationValue = InputValidationRules.NormalizeForSqlInspection(parameter, value);
        if (_options.DetectSqlInjection &&
            !InputValidationRules.ShouldSkipSqlInspection(parameter) &&
            _sqlInjectionPattern.IsMatch(sqlValidationValue))
        {
            return InputValidationResult.Invalid($"SQL injection attempt detected in {parameter.ParamType} parameter '{parameter.Name}'");
        }

        if (_options.DetectXss && _xssPattern.IsMatch(value))
        {
            return InputValidationResult.Invalid($"XSS attempt detected in {parameter.ParamType} parameter '{parameter.Name}'");
        }

        if (_options.DetectCommandInjection && _commandInjectionPattern.IsMatch(value))
        {
            return InputValidationResult.Invalid($"Command injection attempt detected in {parameter.ParamType} parameter '{parameter.Name}'");
        }

        if (_options.DetectPathTraversal &&
            !InputValidationRules.ShouldSkipPathTraversalInspection(parameter, value) &&
            _pathTraversalPattern.IsMatch(value))
        {
            return InputValidationResult.Invalid($"Path traversal attempt detected in {parameter.ParamType} parameter '{parameter.Name}'");
        }

        if (_options.DetectLdapInjection &&
            !InputValidationRules.ShouldSkipLdapInspection(parameter, value) &&
            InputValidationRules.ContainsPotentialLdapInjection(value))
        {
            return InputValidationResult.Invalid($"LDAP injection attempt detected in {parameter.ParamType} parameter '{parameter.Name}'");
        }

        return ValidateCharacterSafety(parameter, value);
    }

    private InputValidationResult ValidateCharacterSafety(ParameterInspectionContext parameter, string value)
    {
        if (_options.DetectNullBytes && value.Contains('\0'))
        {
            return InputValidationResult.Invalid($"Null byte detected in {parameter.ParamType} parameter '{parameter.Name}'");
        }

        if (_options.DetectControlCharacters && InputValidationRules.ContainsControlCharacters(value))
        {
            return InputValidationResult.Invalid($"Control characters detected in {parameter.ParamType} parameter '{parameter.Name}'");
        }

        return !InputValidationRules.IsGeometryServiceGeometryField(parameter)
            && !InputValidationRules.IsGeoServicesProtocolPayloadField(parameter)
            && value.AsSpan().TrimStart().StartsWith('<')
            && RemoteXmlDocumentParser.ExceedsElementDepthLimit(value, RemoteXmlDocumentParser.DefaultMaxElementDepth)
            ? InputValidationResult.Invalid(
                $"XML element nesting depth exceeds maximum allowed depth of {RemoteXmlDocumentParser.DefaultMaxElementDepth} in {parameter.ParamType} parameter '{parameter.Name}'",
                isSuspicious: false)
            : InputValidationResult.Valid();
    }

    // Bound allocation before protocol parsing; UTF-8 byte size, not UTF-16 character
    // count, is the published geometry size limit.
    private InputValidationResult? CheckGeometrySize(string name, string value)
        => value.Length > _geometryLimits.MaxGeometrySize ||
           Encoding.UTF8.GetByteCount(value) > _geometryLimits.MaxGeometrySize
            ? InputValidationResult.Invalid(
                $"Geometry parameter '{name}' exceeds {_geometryLimits.MaxGeometrySize} UTF-8 bytes (Limits:Geometry:MaxGeometrySize).",
                isSuspicious: false)
            : null;

    private static InputValidationResult ValidateHeaderValue(string headerName, Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            // Validate IP address format for IP-related headers
            if ((headerName.Contains("IP", StringComparison.OrdinalIgnoreCase) ||
                 headerName.Contains("For", StringComparison.OrdinalIgnoreCase))
                && !InputValidationRules.IsValidIpAddressList(value))
            {
                return InputValidationResult.Invalid($"Invalid IP address format in header '{headerName}'");
            }

            // Check for header injection attempts
            if (value.AsSpan().ContainsAny('\r', '\n'))
            {
                return InputValidationResult.Invalid($"Header injection attempt detected in '{headerName}'");
            }
        }

        return InputValidationResult.Valid();
    }

    private static bool IsSecuritySensitiveHeader(string headerName)
    {
        return _securitySensitiveHeaders.Contains(headerName);
    }

    private static bool IsOpaqueCredentialHeader(string headerName)
    {
        return _opaqueCredentialHeaders.Contains(headerName);
    }

    private static async Task CreateSecurityErrorResponse(HttpContext context, string message)
    {
        HttpResponseClearHelper.ClearPreservingCorsHeaders(context.Response);
        await StandardErrorResponseFormatter.WriteErrorAsync(
            context,
            StandardErrorResponse.BadRequest(message));
    }
}

public sealed class InputValidationOptions
{
    public const string SectionName = "InputValidation";

    public bool Enabled { get; set; } = true;

    public bool DetectSqlInjection { get; set; } = true;

    public bool DetectXss { get; set; } = true;

    public bool DetectCommandInjection { get; set; } = true;

    public bool DetectPathTraversal { get; set; } = true;

    public bool DetectLdapInjection { get; set; } = true;

    public bool DetectNullBytes { get; set; } = true;

    public bool DetectControlCharacters { get; set; } = true;

    public bool ValidateSuspiciousHeaders { get; set; }

    // A geometry parameter declared by the endpoint uses the geometry byte and vertex budgets; the ordinary text length limit does not apply, and the security checks stay on.
    public int MaxParameterLength { get; set; } = 8192;

    public string[] ExcludedPaths { get; set; } = Array.Empty<string>();
}

internal readonly record struct InputValidationResult(bool IsValid, string? ErrorMessage = null, bool IsSuspicious = false)
{
    public static InputValidationResult Valid() => new(true);
    public static InputValidationResult Invalid(string errorMessage, bool isSuspicious = true) => new(false, errorMessage, isSuspicious);
}

public static class InputValidationMiddlewareExtensions
{
    public static IServiceCollection AddInputValidation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<InputValidationOptions>(
            configuration.GetSection(InputValidationOptions.SectionName));

        return services;
    }

    public static IApplicationBuilder UseInputValidation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<InputValidationMiddleware>();
    }
}
