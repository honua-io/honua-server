// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Security;

/// <summary>
/// Default <see cref="IRequestSecretReferenceResolver"/>: applies the whole-string grammar and the
/// operator policy, then delegates permitted references to the registered
/// <see cref="IConnectionSecretResolver"/>.
/// </summary>
public sealed partial class RequestSecretReferenceResolver : IRequestSecretReferenceResolver
{
    private const string MalformedReason =
        "Secret references must be a single 'provider:identifier' value.";

    private readonly IConnectionSecretResolver _inner;
    private readonly RequestSecretReferenceOptions _options;
    private readonly ILogger<RequestSecretReferenceResolver> _logger;

    /// <summary>Creates the resolver.</summary>
    /// <param name="inner">Provider-dispatching resolver used once a reference is permitted.</param>
    /// <param name="options">Operator policy. Invalid entries are reported and never match.</param>
    /// <param name="logger">Logger for operator-facing diagnostics.</param>
    public RequestSecretReferenceResolver(
        IConnectionSecretResolver inner,
        RequestSecretReferenceOptions options,
        ILogger<RequestSecretReferenceResolver> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // An invalid entry can never match a grammar-valid reference, so it is inert; report it
        // so the operator can correct the setting instead of failing every request.
        foreach (var error in options.Validate())
        {
            LogInvalidPolicyEntry(_logger, error);
        }
    }

    /// <inheritdoc />
    public RequestSecretReferenceDecision Evaluate(string? reference)
    {
        if (!RequestSecretReference.TryParse(reference, out var parsed))
        {
            return RequestSecretReferenceDecision.Refused(MalformedReason);
        }

        return IsPermitted(parsed)
            ? RequestSecretReferenceDecision.Permitted()
            : RequestSecretReferenceDecision.Refused(RequestSecretReferenceException.ClientSafeMessage);
    }

    /// <inheritdoc />
    public async Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!RequestSecretReference.TryParse(reference, out var parsed))
        {
            LogRefused(_logger, "malformed");
            throw new RequestSecretReferenceException();
        }

        if (!IsPermitted(parsed))
        {
            LogRefused(_logger, parsed.Provider);
            throw new RequestSecretReferenceException();
        }

        string? value;
        try
        {
            value = await _inner.ResolveSecretAsync(parsed.Canonical, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Secret providers can use cancellation to report their own timeout even when the
            // caller's request is still active. Preserve the client-safe resolution contract for
            // those failures, while the filter below lets caller cancellation propagate.
            LogResolutionFailed(_logger, parsed.Provider, ex);
            throw new RequestSecretReferenceException();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The provider failure stays in the operator log; the caller only sees the
            // client-safe message so a response cannot describe the secret store.
            LogResolutionFailed(_logger, parsed.Provider, ex);
            throw new RequestSecretReferenceException();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            LogResolutionFailed(_logger, parsed.Provider, null);
            throw new RequestSecretReferenceException();
        }

        return value;
    }

    private bool IsPermitted(RequestSecretReference reference)
    {
        if (!_options.HasEntries)
        {
            return false;
        }

        if (reference.IsEnvironment)
        {
            var name = reference.Identifier;
            if (_options.AllowedEnvironmentVariables.Contains(name, StringComparer.Ordinal))
            {
                return true;
            }

            // Prefixes never reach configuration-binding variables (Section__Key).
            return !name.Contains("__", StringComparison.Ordinal) &&
                   _options.AllowedEnvironmentVariablePrefixes.Any(
                       prefix => name.StartsWith(prefix, StringComparison.Ordinal));
        }

        if (!_inner.GetSupportedProviders().Contains(reference.Provider, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return _options.AllowedSecretReferencePrefixes.Any(prefix =>
            RequestSecretReference.TryParse(prefix, out var allowed) &&
            string.Equals(allowed.Provider, reference.Provider, StringComparison.Ordinal) &&
            reference.Identifier.StartsWith(allowed.Identifier, StringComparison.Ordinal));
    }

    [LoggerMessage(EventId = 4449, Level = LogLevel.Error,
        Message = "Ignoring an invalid request secret reference policy entry: {Error}")]
    private static partial void LogInvalidPolicyEntry(ILogger logger, string error);

    [LoggerMessage(EventId = 4450, Level = LogLevel.Warning,
        Message = "Refused a request-supplied secret reference (provider: {Provider}). Permit it under Security:RequestSecretReferences if it is intended.")]
    private static partial void LogRefused(ILogger logger, string provider);

    [LoggerMessage(EventId = 4451, Level = LogLevel.Warning,
        Message = "A permitted request-supplied secret reference could not be resolved (provider: {Provider}).")]
    private static partial void LogResolutionFailed(ILogger logger, string provider, Exception? exception);
}

/// <summary>
/// Registration helpers for request-supplied secret reference resolution.
/// </summary>
public static class RequestSecretReferenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRequestSecretReferenceResolver"/> bound to the
    /// <c>Security:RequestSecretReferences</c> section. Safe to call more than once.
    /// </summary>
    public static IServiceCollection AddRequestSecretReferenceResolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IRequestSecretReferenceResolver>(serviceProvider =>
            new RequestSecretReferenceResolver(
                serviceProvider.GetRequiredService<IConnectionSecretResolver>(),
                BindOptions(configuration),
                serviceProvider.GetRequiredService<ILogger<RequestSecretReferenceResolver>>()));

        return services;
    }

    /// <summary>
    /// Binds <see cref="RequestSecretReferenceOptions"/> without reflection-based binding so the
    /// path stays AOT-safe.
    /// </summary>
    public static RequestSecretReferenceOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(RequestSecretReferenceOptions.SectionName);
        return new RequestSecretReferenceOptions
        {
            AllowedEnvironmentVariables = ReadList(section, nameof(RequestSecretReferenceOptions.AllowedEnvironmentVariables)),
            AllowedEnvironmentVariablePrefixes = ReadList(section, nameof(RequestSecretReferenceOptions.AllowedEnvironmentVariablePrefixes)),
            AllowedSecretReferencePrefixes = ReadList(section, nameof(RequestSecretReferenceOptions.AllowedSecretReferencePrefixes))
        };
    }

    private static string[] ReadList(IConfigurationSection section, string key)
        => section.GetSection(key).GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
}
