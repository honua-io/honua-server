// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Middleware for comprehensive input validation to prevent injection attacks.
/// Validates request parameters, headers, and query strings for malicious patterns.
/// </summary>
internal sealed class InputValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<InputValidationMiddleware> _logger;
    private readonly InputValidationOptions _options;

    // Common injection patterns to detect and block
    private static readonly Regex _sqlInjectionPattern = new(
        @"(\bunion\s+(?:all\s+)?select\b)|(\bselect\b.+\bfrom\b)|(\binsert\s+into\b)" +
        @"|(\bupdate\s+[\w\.\[\]""`]+\s+set\b)|(\bdelete\s+from\b)" +
        @"|(\bdrop\s+(?:table|database|schema|view|index)\b)" +
        @"|(\b(?:create|alter)\s+(?:table|database|schema|view|index|procedure|function)\b)" +
        @"|(\bexec(?:ute)?\s+[\w\.\[\]""`]+)" +
        @"|(\-\-|\/\*|\*\/)|(\bor\b\s+\d+\s*=\s*\d+)" +
        @"|(\bor\b\s+['""][^'""]*['""]\s*=\s*['""][^'""]*['""])" +
        @"|(;\s*(?:select|insert|update|delete|drop|create|alter|exec|execute)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _xssPattern = new(
        @"<script[^>]*>.*?</script>|<iframe[^>]*>.*?</iframe>|javascript:|vbscript:|onload\s*=|onclick\s*=|onerror\s*=" +
        @"|eval\s*\(|expression\s*\(|<object[^>]*>|<embed[^>]*>|<applet[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex _commandInjectionPattern = new(
        @"(\|\s*(cat|ls|rm|mv|cp|chmod|chown|wget|curl|bash|sh|zsh|cmd|powershell|pwsh|python|perl|php|node|nc|netcat)\b)" +
        @"|(;\s*(cat|ls|rm|mv|cp|chmod|chown|wget|curl|bash|sh|zsh|cmd|powershell|pwsh|python|perl|php|node|nc|netcat)\b)" +
        @"|(&&\s*(cat|ls|rm|mv|cp|chmod|chown|wget|curl|bash|sh|zsh|cmd|powershell|pwsh|python|perl|php|node|nc|netcat)\b)" +
        @"|(\$\(.*\))|(cmd\s+/c)|(powershell)|(\beval\b)|(\bexec\b)|(\bsystem\b)" +
        @"|(\bshell_exec\b)|(\bpassthru\b)|(\bpopen\b)|(\bproc_open\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _pathTraversalPattern = new(
        @"(\.{2}[/\\])|(\.\.[/\\])|([/\\]\.\.[/\\])|(\.\.[/\\]\.\.)|(\.{3,})|(\%2e\%2e)|(\%252e\%252e)" +
        @"|(\.%2f)|(\.\%5c)|(\.%252f)|(\.%255c)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _odataSystemOptionPattern = new(
        @"\$(select|filter|orderby|top|skip|count|expand|levels|search|compute|apply|deltatoken|format)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Suspicious header patterns
    private static readonly HashSet<string> _dangerousHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Forwarded-For", // Can be spoofed
        "X-Real-IP",      // Can be spoofed
        "X-Originating-IP",
        "CF-Connecting-IP",
        "True-Client-IP",
        "X-Cluster-Client-IP"
    };

    private static readonly HashSet<string> _securitySensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-API-Key",
        "Authorization",
        "X-Correlation-ID",
        "X-Forwarded-Proto",
        "X-Forwarded-Host"
    };

    private static readonly HashSet<string> _opaqueCredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "X-API-Key"
    };

    public InputValidationMiddleware(
        RequestDelegate next,
        ILogger<InputValidationMiddleware> logger,
        IOptions<InputValidationOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
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

        // Validate request inputs
        var validationResult = ValidateRequest(context.Request);
        if (!validationResult.IsValid)
        {
            InputValidationLog.MaliciousInputDetected(_logger,
                validationResult.ErrorMessage,
                context.Connection.RemoteIpAddress?.ToString());

            await CreateSecurityErrorResponse(context, validationResult.ErrorMessage ?? "Input validation failed");
            return;
        }

        await _next(context);
    }

    private bool ShouldSkipValidation(PathString path)
    {
        var pathValue = path.Value?.ToLowerInvariant();
        return pathValue?.StartsWith("/health", StringComparison.Ordinal) == true ||
               pathValue?.StartsWith("/metrics", StringComparison.Ordinal) == true ||
               pathValue?.StartsWith("/ready", StringComparison.Ordinal) == true ||
               _options.ExcludedPaths.Any(excluded =>
                   pathValue?.StartsWith(excluded, StringComparison.OrdinalIgnoreCase) == true);
    }

    private InputValidationResult ValidateRequest(HttpRequest request)
    {
        // Validate query parameters
        foreach (var param in request.Query)
        {
            if (string.IsNullOrEmpty(param.Key))
                continue;

            var result = ValidateParameter(request, "query", param.Key, param.Value);
            if (!result.IsValid)
                return result;
        }

        // Validate form data if applicable
        if (request.HasFormContentType && !IsMultipartFormData(request.ContentType) && request.Form != null)
        {
            foreach (var param in request.Form)
            {
                if (string.IsNullOrEmpty(param.Key))
                    continue;

                var result = ValidateParameter(request, "form", param.Key, param.Value);
                if (!result.IsValid)
                    return result;
            }
        }

        // Validate headers for suspicious patterns
        foreach (var header in request.Headers)
        {
            if (string.IsNullOrEmpty(header.Key))
                continue;

            // Check for dangerous headers that can be spoofed
            if (_dangerousHeaders.Contains(header.Key) && _options.ValidateSuspiciousHeaders)
            {
                var result = ValidateHeaderValue(header.Key, header.Value);
                if (!result.IsValid)
                    return result;
            }

            // Validate specific security-sensitive headers
            if (IsSecuritySensitiveHeader(header.Key))
            {
                var result = IsOpaqueCredentialHeader(header.Key)
                    ? ValidateOpaqueCredentialHeaderValue(header.Key, header.Value)
                    : ValidateParameter(request, "header", header.Key, header.Value);
                if (!result.IsValid)
                    return result;
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
                continue;

            if (value.Length > _options.MaxParameterLength)
            {
                return InputValidationResult.Invalid($"Header '{headerName}' exceeds maximum length of {_options.MaxParameterLength}");
            }

            if (value.Contains('\r') || value.Contains('\n'))
            {
                return InputValidationResult.Invalid($"Header injection attempt detected in '{headerName}'");
            }

            if (_options.DetectNullBytes && value.Contains('\0'))
            {
                return InputValidationResult.Invalid($"Null byte detected in header parameter '{headerName}'");
            }

            if (_options.DetectControlCharacters && ContainsControlCharacters(value))
            {
                return InputValidationResult.Invalid($"Control characters detected in header parameter '{headerName}'");
            }
        }

        return InputValidationResult.Valid();
    }

    private InputValidationResult ValidateParameter(
        HttpRequest request,
        string paramType,
        string name,
        Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
                continue;

            // Length validation
            if (value.Length > _options.MaxParameterLength)
            {
                return InputValidationResult.Invalid($"Parameter '{name}' exceeds maximum length of {_options.MaxParameterLength}");
            }

            var sqlValidationValue = NormalizeForSqlInspection(request, paramType, name, value);

            // SQL Injection detection.
            // Skip feature-edit attribute-data payloads (applyEdits/addFeatures/updateFeatures
            // adds/updates/deletes/features/edits). Those carry user attribute VALUES that are
            // bound as SQL parameters (never concatenated into SQL text), so legitimate values
            // such as "Paris--London" or "note /* internal */ ok" are harmless and must not be
            // rejected. SQL/where/orderBy/expression contexts are still inspected below.
            if (_options.DetectSqlInjection &&
                !ShouldSkipSqlInspection(request, paramType, name) &&
                _sqlInjectionPattern.IsMatch(sqlValidationValue))
            {
                return InputValidationResult.Invalid($"SQL injection attempt detected in {paramType} parameter '{name}'");
            }

            // XSS detection
            if (_options.DetectXss && _xssPattern.IsMatch(value))
            {
                return InputValidationResult.Invalid($"XSS attempt detected in {paramType} parameter '{name}'");
            }

            // Command injection detection
            if (_options.DetectCommandInjection && _commandInjectionPattern.IsMatch(value))
            {
                return InputValidationResult.Invalid($"Command injection attempt detected in {paramType} parameter '{name}'");
            }

            // Path traversal detection
            if (_options.DetectPathTraversal &&
                !ShouldSkipPathTraversalInspection(request, paramType, name, value) &&
                _pathTraversalPattern.IsMatch(value))
            {
                return InputValidationResult.Invalid($"Path traversal attempt detected in {paramType} parameter '{name}'");
            }

            // LDAP injection detection
            if (_options.DetectLdapInjection &&
                !ShouldSkipLdapInspection(request, paramType, name) &&
                ContainsPotentialLdapInjection(value))
            {
                return InputValidationResult.Invalid($"LDAP injection attempt detected in {paramType} parameter '{name}'");
            }

            // Null byte detection
            if (_options.DetectNullBytes && value.Contains('\0'))
            {
                return InputValidationResult.Invalid($"Null byte detected in {paramType} parameter '{name}'");
            }

            // Unicode control character detection
            if (_options.DetectControlCharacters && ContainsControlCharacters(value))
            {
                return InputValidationResult.Invalid($"Control characters detected in {paramType} parameter '{name}'");
            }
        }

        return InputValidationResult.Valid();
    }

    // Feature-edit payload fields that carry attribute DATA values rather than SQL/where text.
    // Values inside these payloads are bound as SQL parameters by the storage layer, so
    // SQL-injection heuristics over their contents produce false positives (e.g. attribute
    // values containing "--", "/* */", ";", or quotes). These cover the GeoServices
    // applyEdits / addFeatures / updateFeatures / deleteFeatures form parameters.
    private static readonly HashSet<string> _featureEditPayloadFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "adds",
        "updates",
        "deletes",
        "features",
        "edits"
    };

    private static bool ShouldSkipSqlInspection(HttpRequest request, string paramType, string name)
        => IsFeatureEditPayloadField(request, paramType, name);

    private static bool IsFeatureEditPayloadField(HttpRequest request, string paramType, string name)
    {
        if (!paramType.Equals("form", StringComparison.Ordinal) &&
            !paramType.Equals("query", StringComparison.Ordinal))
        {
            return false;
        }

        if (!_featureEditPayloadFields.Contains(name))
        {
            return false;
        }

        // Restrict to GeoServices FeatureServer edit endpoints so the exemption cannot be
        // abused on unrelated routes. Edit operations (applyEdits / addFeatures /
        // updateFeatures / deleteFeatures) all live under a .../FeatureServer/... path,
        // whether reached via the canonical "/rest/services/{service}/FeatureServer" route
        // or a friendly service alias such as "/{service}/FeatureServer".
        return request.Path.Value?.Contains("/FeatureServer", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string NormalizeForSqlInspection(HttpRequest request, string paramType, string name, string value)
    {
        if (!IsODataSystemQueryOption(request, paramType, name))
        {
            return value;
        }

        return _odataSystemOptionPattern.Replace(value, string.Empty);
    }

    private static bool ShouldSkipLdapInspection(HttpRequest request, string paramType, string name)
        => IsODataSystemQueryOption(request, paramType, name) ||
           IsProtocolFilterQueryOption(request, paramType, name);

    private static bool ShouldSkipPathTraversalInspection(HttpRequest request, string paramType, string name, string value)
        => IsProtocolDatetimeQueryOption(request, paramType, name) &&
           IsOpenStartDatetimeInterval(value);

    private static bool IsOpenStartDatetimeInterval(string value)
    {
        // OGC API and STAC datetime use "../end" for an open-start interval.
        // Keep this exception narrower than the full datetime grammar so generic
        // path values such as "../etc/passwd" are still rejected before endpoint dispatch.
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        return parts is ["..", var end] &&
               DateTimeOffset.TryParse(
                   end,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out _);
    }

    private static bool IsProtocolDatetimeQueryOption(HttpRequest request, string paramType, string name)
    {
        if (!paramType.Equals("query", StringComparison.Ordinal) ||
            !name.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = request.Path.Value;
        return path?.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) == true ||
               path?.StartsWith("/stac", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsODataSystemQueryOption(HttpRequest request, string paramType, string name)
    {
        if (!paramType.Equals("query", StringComparison.Ordinal))
        {
            return false;
        }

        return name.StartsWith('$') &&
               request.Path.Value?.StartsWith("/odata", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsProtocolFilterQueryOption(HttpRequest request, string paramType, string name)
    {
        if (!paramType.Equals("query", StringComparison.Ordinal))
        {
            return false;
        }

        var path = request.Path.Value;
        if (name.Equals("filter", StringComparison.OrdinalIgnoreCase))
        {
            return path?.StartsWith("/stac", StringComparison.OrdinalIgnoreCase) == true ||
                   path?.StartsWith("/ogc/features", StringComparison.OrdinalIgnoreCase) == true ||
                   path?.Contains("/wfs", StringComparison.OrdinalIgnoreCase) == true ||
                   path?.Contains("/wms", StringComparison.OrdinalIgnoreCase) == true;
        }

        return name.Equals("where", StringComparison.OrdinalIgnoreCase) &&
               path?.StartsWith("/rest/services", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static InputValidationResult ValidateHeaderValue(string headerName, Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
                continue;

            // Validate IP address format for IP-related headers
            if (headerName.Contains("IP", StringComparison.OrdinalIgnoreCase) ||
                headerName.Contains("For", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsValidIpAddressList(value))
                {
                    return InputValidationResult.Invalid($"Invalid IP address format in header '{headerName}'");
                }
            }

            // Check for header injection attempts
            if (value.Contains('\r') || value.Contains('\n'))
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

    private static bool ContainsControlCharacters(string value)
    {
        return value.Any(c => char.IsControl(c) && c != '\t' && c != '\r' && c != '\n');
    }

    private static bool ContainsPotentialLdapInjection(string value)
    {
        ReadOnlySpan<string> suspiciousTokens =
        [
            "(|",
            "(&",
            "(!",
            "|(",
            "&(",
            "!(",
            "*(",
            "*)",
            "*=",
            ")=",
            "(=",
            "))",
            "(("
        ];

        foreach (var token in suspiciousTokens)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidIpAddressList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var ips = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var ip in ips)
        {
            var trimmedIp = ip.Trim();
            if (!System.Net.IPAddress.TryParse(trimmedIp, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMultipartFormData(string? contentType)
    {
        return !string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CreateSecurityErrorResponse(HttpContext context, string message)
    {
        context.Response.Clear();
        await StandardErrorResponseFormatter.WriteErrorAsync(
            context,
            StandardErrorResponse.BadRequest(message));
    }
}

/// <summary>
/// Configuration options for input validation middleware.
/// </summary>
public sealed class InputValidationOptions
{
    /// <summary>
    /// Configuration section name for binding.
    /// </summary>
    public const string SectionName = "InputValidation";

    /// <summary>
    /// Whether input validation is enabled. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to detect SQL injection attempts. Default: true.
    /// </summary>
    public bool DetectSqlInjection { get; set; } = true;

    /// <summary>
    /// Whether to detect XSS attempts. Default: true.
    /// </summary>
    public bool DetectXss { get; set; } = true;

    /// <summary>
    /// Whether to detect command injection attempts. Default: true.
    /// </summary>
    public bool DetectCommandInjection { get; set; } = true;

    /// <summary>
    /// Whether to detect path traversal attempts. Default: true.
    /// </summary>
    public bool DetectPathTraversal { get; set; } = true;

    /// <summary>
    /// Whether to detect LDAP injection attempts. Default: true.
    /// </summary>
    public bool DetectLdapInjection { get; set; } = true;

    /// <summary>
    /// Whether to detect null bytes in input. Default: true.
    /// </summary>
    public bool DetectNullBytes { get; set; } = true;

    /// <summary>
    /// Whether to detect control characters in input. Default: true.
    /// </summary>
    public bool DetectControlCharacters { get; set; } = true;

    /// <summary>
    /// Whether to validate suspicious headers that can be spoofed. Default: false.
    /// </summary>
    public bool ValidateSuspiciousHeaders { get; set; }

    /// <summary>
    /// Maximum length allowed for any parameter value. Default: 8192.
    /// </summary>
    public int MaxParameterLength { get; set; } = 8192;

    /// <summary>
    /// List of paths to exclude from validation (e.g., file upload endpoints).
    /// </summary>
    public string[] ExcludedPaths { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Result of input validation.
/// </summary>
internal sealed class InputValidationResult
{
    public bool IsValid { get; }
    public string? ErrorMessage { get; }

    private InputValidationResult(bool isValid, string? errorMessage = null)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static InputValidationResult Valid() => new(true);
    public static InputValidationResult Invalid(string errorMessage) => new(false, errorMessage);
}

/// <summary>
/// Extension methods for registering input validation middleware.
/// </summary>
public static class InputValidationMiddlewareExtensions
{
    /// <summary>
    /// Adds input validation services to the dependency injection container.
    /// </summary>
    public static IServiceCollection AddInputValidation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<InputValidationOptions>(
            configuration.GetSection(InputValidationOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Adds input validation middleware to the pipeline.
    /// Should be added early, but after CORS and before authentication.
    /// </summary>
    public static IApplicationBuilder UseInputValidation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<InputValidationMiddleware>();
    }
}
