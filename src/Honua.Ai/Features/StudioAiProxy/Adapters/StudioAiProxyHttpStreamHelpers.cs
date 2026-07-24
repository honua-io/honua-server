// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.StudioAiProxy.Adapters;

/// <summary>
/// Shared request/read helpers for the HTTP-based streaming adapters (Anthropic, OpenAI-compatible).
/// Factored out so both adapters' async-iterator <c>StreamAsync</c> methods never need a
/// <c>yield return</c> inside a try/catch — C# forbids that — by doing the try/catch here, in
/// ordinary (non-iterator) async methods, and reporting the outcome back as data.
/// </summary>
internal static class StudioAiProxyHttpStreamHelpers
{
    /// <summary>
    /// Sends <paramref name="request"/> and classifies the outcome. <paramref name="linkedToken"/>
    /// should combine <paramref name="callerToken"/> with the provider's configured timeout so a
    /// caller-driven cancellation (real abort — rethrown) can be told apart from a provider-side
    /// timeout (surfaced as <c>TimedOut</c> rather than thrown).
    /// </summary>
    public static async Task<(HttpResponseMessage? Response, bool TimedOut, HttpRequestException? RequestError)> SendSafeAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken)
                .ConfigureAwait(false);
            return (response, false, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, true, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, false, ex);
        }
    }

    /// <summary>Reads one line, classifying timeout/transport failure the same way as <see cref="SendSafeAsync"/>.</summary>
    public static async Task<(string? Line, bool TimedOut, IOException? IoError)> ReadLineSafeAsync(
        StreamReader reader,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            var line = await reader.ReadLineAsync(linkedToken).ConfigureAwait(false);
            return (line, false, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, true, null);
        }
        catch (IOException ex)
        {
            return (null, false, ex);
        }
    }
}
