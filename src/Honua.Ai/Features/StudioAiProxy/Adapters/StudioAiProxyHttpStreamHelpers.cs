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
    internal const int MaximumErrorBodyBytes = 4096;

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

    /// <summary>Opens a response stream under the same provider deadline as send and reads.</summary>
    public static async Task<(Stream? Stream, bool TimedOut, Exception? Failure)> OpenStreamSafeAsync(
        HttpContent content,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            return (await content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false), false, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, false, ex);
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaximumErrorBodyBytes"/> without allowing an error response to
    /// escape the provider deadline. The returned body is diagnostic-only and may be truncated.
    /// </summary>
    public static async Task<(string Body, bool TimedOut, Exception? Failure, int BytesBuffered)> ReadErrorBodySafeAsync(
        HttpContent content,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        var (stream, timedOut, failure) = await OpenStreamSafeAsync(content, linkedToken, callerToken).ConfigureAwait(false);
        if (timedOut || failure is not null)
        {
            return (string.Empty, timedOut, failure, 0);
        }

        var responseStream = stream!;
        await using (responseStream)
        {
            var buffer = new byte[MaximumErrorBodyBytes];
            var total = 0;
            try
            {
                while (total < buffer.Length)
                {
                    var read = await responseStream.ReadAsync(buffer.AsMemory(total), linkedToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                return (System.Text.Encoding.UTF8.GetString(buffer, 0, total), false, null, total);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return (string.Empty, true, null, total);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return (string.Empty, false, ex, total);
            }
        }
    }
}
