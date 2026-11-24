// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security;

internal sealed partial class InputValidationMiddleware
{
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

    // Matches a balanced single-quoted SQL string literal, including doubled-quote
    // ('') escapes. Used to mask literal CONTENT (data, bound as a parameter) in
    // filter expressions before SQL-injection inspection so harmless in-literal
    // sequences ("--", "/* */", ";") are not flagged. Unbalanced quotes (the actual
    // injection vector) do not match and stay visible to the injection pattern.
    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex QuotedStringLiteralPattern();

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

    // A WKT POINT/MULTIPOINT built only from numbers and WKT punctuation. No LDAP filter operator
    // (| & ! * =) can appear in a value this matches.
    [GeneratedRegex(@"^\s*(?:MULTI)?POINT\s*(?:Z\s*)?\(\s*[-+0-9.e\s,()]+\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EdrWktCoordsPattern();

    [GeneratedRegex(@"\$(select|filter|orderby|top|skip|count|expand|levels|search|compute|apply|deltatoken|format)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ODataSystemOptionPattern();

    private static readonly SearchValues<string> _ldapSuspiciousTokens = SearchValues.Create(
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
        ],
        StringComparison.Ordinal);

    private static readonly SearchValues<char> _disallowedControlChars = CreateDisallowedControlChars();

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
        if (endpoint is not null
            && context.Request.HasFormContentType
            && !IsMultipartFormData(context.Request.ContentType))
        {
            try
            {
                await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (InvalidDataException)
            {
                // The form parser rejects malformed values (including encoded NUL)
                // before parameter validation can run. These are bad requests.
                InputValidationLog.InputRejected(_logger, "Invalid form data.");
                await CreateSecurityErrorResponse(context, "Invalid form data.");
                return;
            }
        }

        // Validate request inputs
        var geometryMetadata = endpoint?.Metadata.GetMetadata<GeometryParameterMetadata>();
        var validationResult = ValidateRequest(context.Request, endpoint is not null, geometryMetadata);
        if (validationResult.IsValid && geometryMetadata != null)
        {
            var body = await geometryMetadata.ReadBodyParametersAsync(context.Request, context.RequestAborted);
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
                    validationResult = ValidateParameter(context.Request, "form", parameter.Key, parameter.Value, geometryMetadata);
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

    private InputValidationResult ValidateRequest(
        HttpRequest request,
        bool hasEndpoint,
        GeometryParameterMetadata? geometryMetadata)
    {
        // Validate query parameters
        foreach (var param in request.Query)
        {
            if (string.IsNullOrEmpty(param.Key))
            {
                continue;
            }

            var result = ValidateParameter(request, "query", param.Key, param.Value, geometryMetadata);
            if (!result.IsValid)
                return result;
        }

        // Validate form data if applicable. An unmatched route has no handler that
        // reads fields; touching Request.Form would buffer the body.
        if (hasEndpoint
            && request.HasFormContentType
            && !IsMultipartFormData(request.ContentType)
            && !(HttpMethods.IsPost(request.Method) && geometryMetadata != null)
            && request.Form != null)
        {
            foreach (var param in request.Form)
            {
                if (string.IsNullOrEmpty(param.Key))
                {
                    continue;
                }

                var result = ValidateParameter(request, "form", param.Key, param.Value, geometryMetadata);
                if (!result.IsValid)
                    return result;
            }
        }

        // Validate headers for suspicious patterns
        foreach (var header in request.Headers)
        {
            if (string.IsNullOrEmpty(header.Key))
            {
                continue;
            }

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
                    : ValidateParameter(request, "header", header.Key, header.Value, geometryMetadata: null);
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
        Microsoft.Extensions.Primitives.StringValues values,
        GeometryParameterMetadata? geometryMetadata)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (geometryMetadata != null && name.Equals(geometryMetadata.ParameterName, StringComparison.OrdinalIgnoreCase))
            {
                if (CheckGeometrySize(name, value) is { } sizeError)
                {
                    return sizeError;
                }
                request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                var geometryError = geometryMetadata.Validate(value, _geometryLimits.MaxVerticesPerGeometry, request.HttpContext.RequestAborted);
                if (geometryError != null)
                {
                    return InputValidationResult.Invalid(geometryError, isSuspicious: false);
                }
            }
            else if (IsGeometryServiceGeometryField(request, paramType, name))
            {
                // GeometryServer POSTs exist for geometry too large for a URL. Bound these
                // fields by the published geometry size, not the generic 8192-character text cap;
                // the handler then applies its geometry count, vertex and overlay budgets.
                if (CheckGeometrySize(name, value) is { } sizeError)
                {
                    return sizeError;
                }
            }
            else if (IsGeoServicesProtocolPayloadField(request, paramType, name))
            {
                // Edit and query payloads are bounded by the edit body limit, not the
                // generic 8192-character text cap. A where clause or object-id list
                // longer than that cap is a valid GeoServices request.
                if (value.Length > _maxProtocolPayloadLength)
                {
                    return InputValidationResult.Invalid(
                        $"Parameter '{name}' exceeds maximum length of {_maxProtocolPayloadLength.ToString(CultureInfo.InvariantCulture)}",
                        isSuspicious: false);
                }
            }
            else if (value.Length > _options.MaxParameterLength)
            {
                return InputValidationResult.Invalid($"Parameter '{name}' exceeds maximum length of {_options.MaxParameterLength}", isSuspicious: false);
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
                !ShouldSkipLdapInspection(request, paramType, name, value) &&
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
    private static readonly FrozenSet<string> _featureEditPayloadFields = new[]
    {
        "adds",
        "updates",
        "deletes",
        "features",
        "edits"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // OAuth2 credential parameters (RFC 7662 introspection, RFC 7009 revocation, and the
    // token-exchange grant) carry opaque access/refresh tokens — including signed JWTs whose
    // base64url segments legitimately contain "--" or "/*"/"*/"-like byte sequences. These
    // values are validated by the portal token services (signature/lookup), never concatenated
    // into SQL text, so the SQL-injection heuristic produces false positives that reject valid
    // tokens (e.g. a JWT signature containing "--" → 400 on /sharing/rest/oauth2/introspect).
    // This mirrors the opaque-credential handling for the Authorization / X-API-Key headers.
    // "token" is also exempt on every route; see IsOpaqueTokenCredentialParameter.
    private static readonly FrozenSet<string> _oauth2CredentialFields = new[]
    {
        "token",
        "token_type_hint",
        "refresh_token",
        "code",
        "client_secret",
        "client_assertion",
        "assertion",
        // Client-owned state and PKCE values are opaque and can contain "--". The broker's
        // generated callback IDs are hexadecimal, but the authorize request preserves the
        // client's original state. These values are compared or hashed, never placed in SQL.
        "state",
        "code_challenge",
        "code_verifier",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool ShouldSkipSqlInspection(HttpRequest request, string paramType, string name)
        => IsFeatureEditPayloadField(request, paramType, name)
           || IsOpaqueTokenCredentialParameter(paramType, name)
           || IsOAuth2CredentialParameter(request, paramType, name)
           || IsGenerateTokenPasswordParameter(request, paramType, name);

    private static bool IsOpaqueTokenCredentialParameter(string paramType, string name)
    {
        // The token query/form parameter is an opaque credential on every route
        // (GeoServices, OGC/WFS, portal, OAuth2). Managed API keys are base64url and
        // may contain "--" or "/*"-like bytes. The verifier looks the value up or
        // compares it; it is never concatenated into SQL. Skip only the SQL heuristic.
        // Size, control-character, XSS, command, path, and LDAP checks still run, matching
        // the reason Authorization and X-API-Key skip SQL inspection.
        return paramType is "form" or "query"
            && name.Equals("token", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenerateTokenPasswordParameter(HttpRequest request, string paramType, string name)
    {
        // Managed API keys are opaque base64url credentials and may contain "--".
        // The credential verifier performs a lookup or constant-time comparison, never
        // SQL evaluation. Keep this exception scoped to the supported password field;
        // all size, control-character and other input checks still run.
        return paramType is "form" or "query"
            && name.Equals("password", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(request.Path.Value, "/sharing/rest/generateToken", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.Path.Value, "/sharing/rest/generateToken/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOAuth2CredentialParameter(HttpRequest request, string paramType, string name)
    {
        if (!paramType.Equals("form", StringComparison.Ordinal) &&
            !paramType.Equals("query", StringComparison.Ordinal))
        {
            return false;
        }

        if (!_oauth2CredentialFields.Contains(name))
        {
            return false;
        }

        // Restrict to the Sharing OAuth2 credential endpoints so the exemption cannot be abused
        // on unrelated routes. Every OAuth2 route lives under "/sharing/rest/oauth2/" (authorize,
        // callback, token, revoke, introspect).
        return request.Path.Value?.Contains("/sharing/rest/oauth2/", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static readonly FrozenSet<string> _geoServicesQueryPayloadFields = new[]
    {
        "objectIds",
        "outFields",
        "where",
        "orderByFields",
        "definitionExpression",
        "quantizationParameters",
        "layerDefs",
        "datumTransformation",
        "outStatistics",
        "groupByFieldsForStatistics",
        "having"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _geometryServiceGeometryFields = new[]
    {
        "geometries",
        "geometries1",
        "geometries2",
        "geometry",
        "geometry1",
        "geometry2",
        "target",
        "cutter",
        "polylines",
        "polygons",
        "reshaper",
        "trimExtendTo",
        "envelope",
        "coordinates",
        "strings"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsGeometryServiceGeometryField(HttpRequest request, string paramType, string name)
        => paramType is "form" or "query"
           && _geometryServiceGeometryFields.Contains(name)
           && request.Path.Value?.Contains("/Utilities/Geometry/GeometryServer/", StringComparison.OrdinalIgnoreCase) == true;

    // Bound allocation before protocol parsing; UTF-8 byte size, not UTF-16 character
    // count, is the published geometry size limit.
    private InputValidationResult? CheckGeometrySize(string name, string value)
        => value.Length > _geometryLimits.MaxGeometrySize ||
           Encoding.UTF8.GetByteCount(value) > _geometryLimits.MaxGeometrySize
            ? InputValidationResult.Invalid(
                $"Geometry parameter '{name}' exceeds {_geometryLimits.MaxGeometrySize} UTF-8 bytes (Limits:Geometry:MaxGeometrySize).",
                isSuspicious: false)
            : null;

    private static bool IsGeoServicesProtocolPayloadField(HttpRequest request, string paramType, string name)
    {
        if (IsFeatureEditPayloadField(request, paramType, name))
        {
            return true;
        }

        if (paramType is not ("form" or "query") ||
            !_geoServicesQueryPayloadFields.Contains(name))
        {
            return false;
        }

        var path = request.Path.Value;
        return path?.Contains("/FeatureServer/", StringComparison.OrdinalIgnoreCase) == true ||
               path?.Contains("/MapServer/", StringComparison.OrdinalIgnoreCase) == true;
    }

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
        if (IsODataSystemQueryOption(request, paramType, name))
        {
            return ODataSystemOptionPattern().Replace(value, string.Empty);
        }

        if (Cql2JsonLiteralMask.AppliesTo(request, paramType, name))
        {
            return Cql2JsonLiteralMask.Mask(value);
        }

        // For SQL/CQL filter-expression parameters (where/having/$filter/filter),
        // mask the CONTENT of balanced single-quoted string literals before SQL-
        // injection inspection. Literal content is bound as a parameter by the
        // storage layer (it is data, not executable), so sequences like "--",
        // "/* */" or ";" inside a quoted literal (e.g. where=name='note -- x' or a
        // LIKE pattern '%/*%') are harmless and must not be rejected. Real injection
        // breaks OUT of the quoting (unbalanced quotes / structure outside literals)
        // and remains visible to the pattern after masking (bug hunt).
        if (IsSqlFilterExpressionParameter(paramType, name))
        {
            return QuotedStringLiteralPattern().Replace(value, "''");
        }

        return value;
    }

    private static bool IsSqlFilterExpressionParameter(string paramType, string name)
    {
        if (!paramType.Equals("form", StringComparison.Ordinal) &&
            !paramType.Equals("query", StringComparison.Ordinal))
        {
            return false;
        }

        return name.Equals("where", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("having", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("$filter", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("filter", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipLdapInspection(HttpRequest request, string paramType, string name, string value)
        => IsODataSystemQueryOption(request, paramType, name) ||
           IsSensorThingsSystemQueryOption(request, paramType, name) ||
           IsProtocolFilterQueryOption(request, paramType, name) ||
           IsPortalSearchQuery(request, paramType, name) ||
           IsEdrWktCoordsQueryOption(request, paramType, name, value);

    private static bool IsPortalSearchQuery(HttpRequest request, string paramType, string name)
        => paramType is "query" or "form"
           && name.Equals("q", StringComparison.OrdinalIgnoreCase)
           && (string.Equals(request.Path.Value, "/sharing/rest/search", StringComparison.OrdinalIgnoreCase)
               || string.Equals(request.Path.Value, "/sharing/rest/search/", StringComparison.OrdinalIgnoreCase));

    private static bool IsEdrWktCoordsQueryOption(HttpRequest request, string paramType, string name, string value)
    {
        // OGC API - EDR position coords are WKT, and the EDR 1.1 MULTIPOINT((x y),(x y)) form carries
        // the "((" and "))" tokens the LDAP heuristic flags. Exempt only a pure WKT value so
        // anything carrying filter syntax is still inspected.
        return paramType.Equals("query", StringComparison.Ordinal) &&
               name.Equals("coords", StringComparison.OrdinalIgnoreCase) &&
               request.Path.Value?.StartsWith("/edr/", StringComparison.OrdinalIgnoreCase) == true &&
               EdrWktCoordsPattern().IsMatch(value);
    }

    private static bool ShouldSkipPathTraversalInspection(HttpRequest request, string paramType, string name, string value)
        => IsProtocolDatetimeQueryOption(request, paramType, name) &&
           IsOpenStartDatetimeInterval(value);

    private static bool IsOpenStartDatetimeInterval(string value)
    {
        // OGC API and STAC datetime use "../end" for an open-start interval.
        // Keep this exception narrower than the full datetime grammar so generic
        // path values such as "../etc/passwd" are still rejected before endpoint dispatch.
        var span = value.AsSpan();
        Span<Range> parts = stackalloc Range[3];
        var count = span.Split(parts, '/', StringSplitOptions.TrimEntries);
        return count == 2 &&
               span[parts[0]].SequenceEqual("..") &&
               DateTimeOffset.TryParse(
                   span[parts[1]],
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

        // OGC API - EDR shares the OGC API datetime grammar, so its position/cube queries accept
        // the same open-start interval.
        var path = request.Path.Value;
        return path?.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) == true ||
               path?.StartsWith("/stac", StringComparison.OrdinalIgnoreCase) == true ||
               path?.StartsWith("/edr/", StringComparison.OrdinalIgnoreCase) == true;
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

    private static bool IsSensorThingsSystemQueryOption(HttpRequest request, string paramType, string name)
    {
        if (!paramType.Equals("query", StringComparison.Ordinal))
        {
            return false;
        }

        // SensorThings $filter / $orderby / $expand carry OData geometry and function
        // text. POLYGON((...)) contains the parenthesis tokens the LDAP heuristic
        // flags, and the value is parsed as OData rather than forwarded to a directory.
        return name.StartsWith('$') &&
               request.Path.Value?.StartsWith("/sta/", StringComparison.OrdinalIgnoreCase) == true;
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
            {
                continue;
            }

            // Validate IP address format for IP-related headers
            if ((headerName.Contains("IP", StringComparison.OrdinalIgnoreCase) ||
                 headerName.Contains("For", StringComparison.OrdinalIgnoreCase))
                && !IsValidIpAddressList(value))
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

    private static bool ContainsControlCharacters(string value)
    {
        return value.AsSpan().ContainsAny(_disallowedControlChars);
    }

    private static SearchValues<char> CreateDisallowedControlChars()
    {
        Span<char> chars = stackalloc char[0x20 + 0x21];
        var count = 0;
        for (var codePoint = 0; codePoint <= 0x1F; codePoint++)
        {
            var ch = (char)codePoint;
            if (ch is not ('\t' or '\r' or '\n'))
            {
                chars[count++] = ch;
            }
        }

        for (var codePoint = 0x7F; codePoint <= 0x9F; codePoint++)
        {
            chars[count++] = (char)codePoint;
        }

        return SearchValues.Create(chars[..count]);
    }

    private static bool ContainsPotentialLdapInjection(string value)
    {
        return value.AsSpan().ContainsAny(_ldapSuspiciousTokens);
    }

    private static bool IsValidIpAddressList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan();
        var hasAny = false;
        foreach (var range in span.Split(','))
        {
            var segment = span[range];
            if (segment.IsEmpty)
            {
                continue;
            }

            hasAny = true;
            if (!System.Net.IPAddress.TryParse(segment.Trim(), out _))
            {
                return false;
            }
        }

        return hasAny;
    }

    private static bool IsMultipartFormData(string? contentType)
    {
        return !string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);
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
