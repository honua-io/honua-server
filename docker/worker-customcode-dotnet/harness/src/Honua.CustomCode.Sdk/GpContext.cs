// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.CustomCode.Sdk;

/// <summary>
/// The object passed to a tool's <see cref="IGeoprocessingTool.ExecuteAsync"/>. This
/// is the .NET mirror of the Python harness's <c>GpContext</c>.
/// </summary>
/// <remarks>
/// Stability note: this is the SDK-facing API. Keep it additive — adding members is
/// fine; renaming/removing breaks tools pinned to an image.
/// </remarks>
public sealed class GpContext
{
    /// <summary>Creates a context. The harness constructs this; tools receive it.</summary>
    /// <param name="parameters">Parsed <c>params_json</c> as a <see cref="JsonElement"/>.</param>
    /// <param name="inputs">Resolved input artifacts (name -&gt; local path).</param>
    /// <param name="client">A pre-authenticated, scoped Honua client (see <see cref="Client"/>).</param>
    /// <param name="output">Sink to register artifacts for upload.</param>
    /// <param name="progress">Coarse progress reporter.</param>
    /// <param name="log">Job logger.</param>
    /// <param name="workDirectory">A writable scratch directory the tool owns for the job.</param>
    public GpContext(
        JsonElement parameters,
        IReadOnlyDictionary<string, string> inputs,
        object? client,
        IOutputSink output,
        IProgressReporter progress,
        IGpLogger log,
        string workDirectory)
    {
        Params = parameters;
        Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        Client = client;
        Output = output ?? throw new ArgumentNullException(nameof(output));
        Progress = progress ?? throw new ArgumentNullException(nameof(progress));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        WorkDirectory = string.IsNullOrWhiteSpace(workDirectory)
            ? throw new ArgumentException("workDirectory is required.", nameof(workDirectory))
            : workDirectory;
    }

    /// <summary>The parsed <c>params_json</c> (a tool-defined schema), as a <see cref="JsonElement"/>.</summary>
    public JsonElement Params { get; }

    /// <summary>
    /// Resolved input artifacts (name -&gt; local path). For Phase 2 this is whatever
    /// the server pre-staged; tools that need server data pull it via <see cref="Client"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; }

    /// <summary>
    /// A pre-authenticated, <em>scoped</em> Honua client. Its bearer token is job-bound
    /// and least-privilege; the raw token is never exposed to tool code. Exposed as
    /// <see cref="object"/> so the SDK contract does not drag the whole Honua SDK graph
    /// through this stable package — a tool casts it to the client type it needs. May be
    /// <see langword="null"/> when no callback access was granted.
    /// </summary>
    public object? Client { get; }

    /// <summary>Sink to register artifacts for upload to the job's output prefix.</summary>
    public IOutputSink Output { get; }

    /// <summary>Coarse progress reporter.</summary>
    public IProgressReporter Progress { get; }

    /// <summary>Job logger.</summary>
    public IGpLogger Log { get; }

    /// <summary>A writable scratch directory the tool owns for the job.</summary>
    public string WorkDirectory { get; }
}
