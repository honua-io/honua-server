// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// Orchestrates the Studio AI proxy: resolves the named (or default) configured provider, dispatches
/// to the adapter registered for its <see cref="StudioAiProxyProviderOptions.Kind"/>, and — as a
/// defense-in-depth backstop over the adapter contract — guarantees the event sequence always ends
/// with exactly one terminal event even if an adapter misbehaves (throws instead of yielding an
/// <see cref="StudioAiChatEventType.Error"/>).
/// </summary>
internal sealed class StudioAiProxyService : IStudioAiProxyService
{
    private readonly StudioAiProxyConfiguration _configuration;
    private readonly Dictionary<string, IStudioAiProxyAdapter> _adaptersByKind;
    private readonly ILogger<StudioAiProxyService> _logger;

    public StudioAiProxyService(
        IOptions<StudioAiProxyConfiguration> options,
        IEnumerable<IStudioAiProxyAdapter> adapters,
        ILogger<StudioAiProxyService> logger)
    {
        _configuration = options.Value;
        _adaptersByKind = adapters
            .GroupBy(a => a.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public bool Enabled => _configuration.Enabled && _configuration.Providers.Count > 0;

    public Task<StudioAiCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled)
        {
            return Task.FromResult(new StudioAiCapabilitiesResponse { Enabled = false });
        }

        var providers = _configuration.Providers
            .Select(kv => BuildCapability(kv.Key, kv.Value))
            .OrderBy(p => p.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(new StudioAiCapabilitiesResponse
        {
            Enabled = true,
            DefaultProvider = _configuration.DefaultProvider,
            Providers = providers
        });
    }

    public string? ValidateRequest(StudioAiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return "The Studio AI proxy is not enabled.";
        }

        if (request.Messages.Count == 0)
        {
            return "At least one message is required.";
        }

        var totalChars = request.Messages.Sum(m => m.Content.Length) + (request.System?.Length ?? 0);
        if (totalChars > _configuration.MaxPromptCharacters)
        {
            return $"Request content exceeds the configured limit of {_configuration.MaxPromptCharacters} characters.";
        }

        if (request.ToolChoice?.Mode == StudioAiToolChoiceMode.Specific && string.IsNullOrWhiteSpace(request.ToolChoice.ToolName))
        {
            return "toolChoice.mode 'specific' requires toolChoice.toolName.";
        }

        var providerName = ResolveProviderName(request);
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return "No provider was named and no default provider is configured.";
        }

        var providerOptions = _configuration.GetProvider(providerName);
        if (providerOptions is null)
        {
            return $"Provider '{providerName}' is not configured.";
        }

        if (!_adaptersByKind.TryGetValue(providerOptions.Kind, out var adapter))
        {
            return $"Provider '{providerName}' declares unsupported kind '{providerOptions.Kind}'.";
        }

        if (!adapter.IsConfigured(providerOptions))
        {
            return $"Provider '{providerName}' is missing required configuration (model or credentials).";
        }

        if (request.Tools is { Count: > 0 } && !providerOptions.SupportsTools)
        {
            return $"Provider '{providerName}' does not support tool calls.";
        }

        return null;
    }

    public async IAsyncEnumerable<StudioAiChatEvent> StreamChatAsync(
        StudioAiChatRequest request,
        StudioAiProxyCallSummary summary,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(summary);

        var providerName = ResolveProviderName(request)!;
        var providerOptions = _configuration.GetProvider(providerName)!;
        var adapter = _adaptersByKind[providerOptions.Kind];
        var model = string.IsNullOrWhiteSpace(request.Model) ? providerOptions.Model : request.Model!;

        summary.Provider = providerName;
        summary.Kind = providerOptions.Kind;
        summary.Model = model;

        StudioAiProxyLog.ChatRequested(_logger, providerName, providerOptions.Kind, model);

        var enumerator = adapter.StreamAsync(providerOptions, request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var sawTerminalEvent = false;

        try
        {
            while (true)
            {
                var (hasNext, failure) = await MoveNextSafeAsync(enumerator, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    summary.Succeeded = false;
                    summary.ErrorMessage = "Adapter failed unexpectedly.";
                    summary.StopReason = StudioAiStopReason.Error;
                    StudioAiProxyLog.ProviderRequestFailed(_logger, providerName, failure);
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.Error,
                        Model = model,
                        ErrorMessage = "The provider adapter failed unexpectedly."
                    };
                    sawTerminalEvent = true;
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                var evt = enumerator.Current;
                if (evt.Type is StudioAiChatEventType.MessageStop or StudioAiChatEventType.Error)
                {
                    sawTerminalEvent = true;
                    ApplySummary(summary, providerName, evt);
                }

                yield return evt;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (!sawTerminalEvent)
        {
            // Contract violation guard: an adapter that ends its sequence without a terminal event
            // (see IStudioAiProxyAdapter remarks) still gets exactly one Error event and one audit
            // record here rather than leaving the client's stream hanging silently.
            summary.Succeeded = false;
            summary.ErrorMessage = "Adapter ended without a terminal event.";
            summary.StopReason = StudioAiStopReason.Error;
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.Error,
                Model = model,
                ErrorMessage = "The provider adapter ended the stream unexpectedly."
            };
        }
    }

    private void ApplySummary(StudioAiProxyCallSummary summary, string providerName, StudioAiChatEvent evt)
    {
        summary.LatencyMs = evt.LatencyMs ?? summary.LatencyMs;

        if (evt.Type == StudioAiChatEventType.MessageStop)
        {
            summary.PromptTokens = evt.PromptTokens;
            summary.CompletionTokens = evt.CompletionTokens;
            summary.StopReason = evt.StopReason;
            summary.Succeeded = true;
            StudioAiProxyLog.ChatCompleted(_logger, providerName, evt.StopReason ?? StudioAiStopReason.EndTurn, summary.LatencyMs);
        }
        else
        {
            summary.Succeeded = false;
            summary.ErrorMessage = evt.ErrorMessage;
            summary.StopReason = StudioAiStopReason.Error;
        }
    }

    private static async Task<(bool HasNext, Exception? Failure)> MoveNextSafeAsync(
        IAsyncEnumerator<StudioAiChatEvent> enumerator,
        CancellationToken callerToken)
    {
        try
        {
            var hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            return (hasNext, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional catch-all: this is the boundary over a plugged-in IStudioAiProxyAdapter, which
        // per its own contract is expected to translate its provider's failures into Error events
        // rather than throw; this is the backstop for an adapter that does not honor that contract.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, ex);
        }
    }

    private string? ResolveProviderName(StudioAiChatRequest request)
        => string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider;

    private StudioAiCapability BuildCapability(string name, StudioAiProxyProviderOptions options)
    {
        var configured = _adaptersByKind.TryGetValue(options.Kind, out var adapter) && adapter.IsConfigured(options);
        return new StudioAiCapability
        {
            Provider = name,
            Kind = options.Kind,
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            ToolSupport = options.SupportsTools,
            Streaming = true,
            IsDefault = string.Equals(name, _configuration.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            Configured = configured
        };
    }
}
