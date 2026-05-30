// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

/// <summary>
/// Builds Content Security Policy (CSP) headers tailored for geospatial API servers.
/// Provides methods to create security policies that protect against XSS and injection attacks
/// while allowing necessary resources for geospatial data visualization and processing.
/// </summary>
internal sealed class ContentSecurityPolicyBuilder
{
    private static readonly string[] _defaultTileServers =
    [
        "*.openstreetmap.org",
        "*.tile.openstreetmap.org",
        "*.tiles.mapbox.com",
        "*.api.mapbox.com",
        "*.esri.com",
        "*.arcgisonline.com",
        "*.arcgis.com"
    ];

    private static readonly string[] _defaultMappingCdns =
    [
        "cdnjs.cloudflare.com",
        "unpkg.com",
        "cdn.jsdelivr.net"
    ];

    private readonly Dictionary<string, HashSet<string>> _directives = new();
    private readonly bool _isDevelopment;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSecurityPolicyBuilder"/> class.
    /// </summary>
    /// <param name="isDevelopment">Whether the application is running in development mode</param>
    public ContentSecurityPolicyBuilder(bool isDevelopment = false)
    {
        _isDevelopment = isDevelopment;
        InitializeDefaults();
    }

    /// <summary>
    /// Creates a CSP policy optimized for geospatial API servers.
    /// This policy is restrictive but allows necessary resources for map rendering and data visualization.
    /// </summary>
    /// <returns>A new builder instance with geospatial API defaults</returns>
    public static ContentSecurityPolicyBuilder ForGeospatialApi(bool isDevelopment = false)
    {
        var builder = new ContentSecurityPolicyBuilder(isDevelopment);

        // Reset to strict geospatial API defaults
        builder._directives.Clear();

        // Default source - only allow same origin for most resources
        builder.AddDirective("default-src", "'self'");

        // Script sources - strict for production, relaxed for development
        if (isDevelopment)
        {
            // Development: Allow unsafe-eval for dev tools, unsafe-inline for debugging
            builder.AddDirective("script-src", "'self'", "'unsafe-eval'", "'unsafe-inline'",
                "localhost:*", "127.0.0.1:*", "*.localhost");
        }
        else
        {
            // Production: Only allow same origin and specific trusted CDNs for mapping libraries
            builder.AddDirective("script-src", "'self'");
        }

        // Style sources - allow unsafe-inline for map libraries that require dynamic styles
        builder.AddDirective("style-src", "'self'", "'unsafe-inline'");

        // Image sources - allow data URLs for inline map icons and tiles from trusted tile servers
        builder.AddDirective("img-src", "'self'", "data:", "blob:");

        // Connection sources - allow same origin and trusted geospatial services
        builder.AddDirective("connect-src", "'self'");

        // Font sources - allow same origin and data URLs for custom map fonts
        builder.AddDirective("font-src", "'self'", "data:");

        // Media sources - allow same origin
        builder.AddDirective("media-src", "'self'");

        // Object sources - completely disallowed for security
        builder.AddDirective("object-src", "'none'");

        // Child sources - for map iframe embedding if needed
        builder.AddDirective("child-src", "'self'");

        // Worker sources - for web workers in mapping libraries
        builder.AddDirective("worker-src", "'self'", "blob:");

        // Frame ancestors - prevent clickjacking
        builder.AddDirective("frame-ancestors", "'none'");

        // Form actions - only allow same origin
        builder.AddDirective("form-action", "'self'");

        // Base URI - prevent base tag injection
        builder.AddDirective("base-uri", "'self'");

        // Manifest source - for PWA manifests
        builder.AddDirective("manifest-src", "'self'");

        return builder;
    }

    /// <summary>
    /// Creates a CSP policy for API-only servers with no browser UI components.
    /// This is the most restrictive policy suitable for pure REST APIs.
    /// </summary>
    /// <returns>A new builder instance with API-only defaults</returns>
    public static ContentSecurityPolicyBuilder ForApiOnly()
    {
        var builder = new ContentSecurityPolicyBuilder();

        // Reset to API-only defaults
        builder._directives.Clear();

        // Extremely restrictive policy for API endpoints
        builder.AddDirective("default-src", "'none'");
        builder.AddDirective("script-src", "'none'");
        builder.AddDirective("style-src", "'none'");
        builder.AddDirective("img-src", "'none'");
        builder.AddDirective("connect-src", "'none'");
        builder.AddDirective("font-src", "'none'");
        builder.AddDirective("media-src", "'none'");
        builder.AddDirective("object-src", "'none'");
        builder.AddDirective("child-src", "'none'");
        builder.AddDirective("worker-src", "'none'");
        builder.AddDirective("frame-ancestors", "'none'");
        builder.AddDirective("form-action", "'none'");
        builder.AddDirective("base-uri", "'none'");
        builder.AddDirective("manifest-src", "'none'");

        return builder;
    }

    /// <summary>
    /// Allows trusted tile servers for map rendering.
    /// Common geospatial tile servers that are generally safe to include.
    /// </summary>
    /// <param name="additionalServers">Additional tile server URLs to allow</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder AllowTileServers(params string[] additionalServers)
    {
        var tileServers = _defaultTileServers.Concat(additionalServers);

        foreach (var server in tileServers)
        {
            AddDirective("img-src", server);
            AddDirective("connect-src", server);
        }

        return this;
    }

    /// <summary>
    /// Allows Content Delivery Networks commonly used for mapping libraries.
    /// </summary>
    /// <param name="additionalCdns">Additional CDN URLs to allow</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder AllowMappingCdns(params string[] additionalCdns)
    {
        var cdns = _defaultMappingCdns.Concat(additionalCdns);

        foreach (var cdn in cdns)
        {
            AddDirective("script-src", cdn);
            AddDirective("style-src", cdn);
            AddDirective("font-src", cdn);
        }

        return this;
    }

    /// <summary>
    /// Allows WebSocket connections for real-time geospatial data.
    /// </summary>
    /// <param name="websocketUrls">WebSocket URLs to allow</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder AllowWebSockets(params string[] websocketUrls)
    {
        foreach (var url in websocketUrls)
        {
            AddDirective("connect-src", url.Replace("http://", "ws://").Replace("https://", "wss://"));
        }

        return this;
    }

    /// <summary>
    /// Allows specific domains for CORS-enabled geospatial APIs.
    /// </summary>
    /// <param name="apiDomains">API domain URLs to allow</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder AllowGeospatialApis(params string[] apiDomains)
    {
        foreach (var domain in apiDomains)
        {
            AddDirective("connect-src", domain);
        }

        return this;
    }

    /// <summary>
    /// Allows inline styles with specific hashes for map libraries that require them.
    /// This is safer than 'unsafe-inline' as it only allows specific inline styles.
    /// </summary>
    /// <param name="styleHashes">SHA256 hashes of allowed inline styles</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder AllowInlineStyleHashes(params string[] styleHashes)
    {
        foreach (var hash in styleHashes)
        {
            var formattedHash = hash.StartsWith("'sha256-") ? hash : $"'sha256-{hash}'";
            AddDirective("style-src", formattedHash);
        }

        return this;
    }

    /// <summary>
    /// Allows inline scripts with specific hashes for essential functionality.
    /// This is much safer than 'unsafe-inline' as it only allows specific scripts.
    /// </summary>
    /// <param name="scriptHashes">SHA256 hashes of allowed inline scripts</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder AllowInlineScriptHashes(params string[] scriptHashes)
    {
        foreach (var hash in scriptHashes)
        {
            var formattedHash = hash.StartsWith("'sha256-") ? hash : $"'sha256-{hash}'";
            AddDirective("script-src", formattedHash);
        }

        return this;
    }

    /// <summary>
    /// Adds a directive with one or more values to the CSP policy.
    /// </summary>
    /// <param name="directive">The CSP directive name (e.g., "script-src")</param>
    /// <param name="values">The values for this directive</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder AddDirective(string directive, params string[] values)
    {
        if (!_directives.TryGetValue(directive, out var directiveValues))
        {
            directiveValues = new HashSet<string>();
            _directives[directive] = directiveValues;
        }

        foreach (var value in values)
        {
            directiveValues.Add(value);
        }

        return this;
    }

    /// <summary>
    /// Removes a directive from the CSP policy.
    /// </summary>
    /// <param name="directive">The directive to remove</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder RemoveDirective(string directive)
    {
        _directives.Remove(directive);
        return this;
    }

    /// <summary>
    /// Removes specific values from a directive.
    /// </summary>
    /// <param name="directive">The directive to modify</param>
    /// <param name="values">The values to remove</param>
    /// <returns>The builder instance for method chaining</returns>
    public ContentSecurityPolicyBuilder RemoveFromDirective(string directive, params string[] values)
    {
        if (_directives.TryGetValue(directive, out var directiveValues))
        {
            foreach (var value in values)
            {
                directiveValues.Remove(value);
            }
        }

        return this;
    }

    /// <summary>
    /// Builds the final CSP header value.
    /// </summary>
    /// <returns>The complete CSP policy string</returns>
    public string Build()
    {
        var policy = string.Join("; ", _directives
            .Where(kvp => kvp.Value.Count > 0)
            .Select(kvp => $"{kvp.Key} {string.Join(" ", kvp.Value)}"));

        return policy;
    }

    /// <summary>
    /// Validates the current CSP policy for common security issues.
    /// </summary>
    /// <returns>A list of validation warnings</returns>
    public IReadOnlyList<string> Validate()
    {
        var warnings = new List<string>();
        var hasScriptSources = _directives.TryGetValue("script-src", out var scriptSources);
        var hasStyleSources = _directives.TryGetValue("style-src", out var styleSources);

        // Check for dangerous 'unsafe-inline' or 'unsafe-eval' in production
        if (!_isDevelopment)
        {
            if (hasScriptSources && scriptSources!.Contains("'unsafe-eval'"))
            {
                warnings.Add("'unsafe-eval' in script-src is dangerous and should be avoided in production");
            }

            if (hasScriptSources && scriptSources!.Contains("'unsafe-inline'"))
            {
                warnings.Add("'unsafe-inline' in script-src is dangerous and should be replaced with nonces or hashes");
            }

            if (hasStyleSources && styleSources!.Contains("'unsafe-inline'"))
            {
                warnings.Add("Consider using style hashes instead of 'unsafe-inline' for better security");
            }
        }

        // Check for missing important directives
        if (!_directives.ContainsKey("default-src"))
        {
            warnings.Add("Missing 'default-src' directive - this is recommended as a fallback");
        }

        if (!_directives.ContainsKey("frame-ancestors"))
        {
            warnings.Add("Missing 'frame-ancestors' directive - this helps prevent clickjacking");
        }

        if (hasScriptSources && scriptSources!.Contains("*"))
        {
            warnings.Add("Wildcard (*) in script-src allows any domain and is highly insecure");
        }

        return warnings.AsReadOnly();
    }

    /// <summary>
    /// Initializes default CSP directives for a basic secure configuration.
    /// </summary>
    private void InitializeDefaults()
    {
        AddDirective("default-src", "'self'");
        AddDirective("script-src", "'self'");
        AddDirective("style-src", "'self'", "'unsafe-inline'");
        AddDirective("img-src", "'self'", "data:");
        AddDirective("connect-src", "'self'");
        AddDirective("font-src", "'self'");
        AddDirective("media-src", "'self'");
        AddDirective("object-src", "'none'");
        AddDirective("frame-ancestors", "'none'");
        AddDirective("form-action", "'self'");
        AddDirective("base-uri", "'self'");
    }
}
