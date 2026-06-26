// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CustomCode.Sdk;

/// <summary>
/// The contract a .NET custom-code GP tool implements — the symmetric counterpart of
/// the Python harness's <c>def execute(context) -&gt; GpResult</c>.
/// </summary>
/// <remarks>
/// A user tool ships an assembly exposing a public type implementing this interface
/// with a public parameterless constructor. The image entrypoint identifies it as
/// <c>"MyAsm::My.Namespace.MyTool"</c> (assembly simple name, <c>::</c>, CLR type).
/// The harness clones the user code at a pinned git SHA, builds the user's
/// <c>.csproj</c> against a warm NuGet cache, strips the IMDS/token environment before
/// activating the type, builds a <see cref="GpContext"/> with a scoped Honua client,
/// calls <see cref="ExecuteAsync"/>, uploads the registered artifacts, and maps the
/// returned <see cref="GpResult"/> to a process exit code.
/// </remarks>
public interface IGeoprocessingTool
{
    /// <summary>
    /// Run the tool. Register output files via <see cref="GpContext.Output"/>, report
    /// coarse progress via <see cref="GpContext.Progress"/>, log via
    /// <see cref="GpContext.Log"/>, and observe <paramref name="cancellationToken"/> at
    /// loop boundaries for cooperative cancellation.
    /// </summary>
    /// <param name="context">The job context (params, inputs, scoped client, sinks, scratch dir).</param>
    /// <param name="cancellationToken">Flipped by the harness when the job is cancelled.</param>
    /// <returns>The terminal result (<see cref="GpResult.Succeeded"/> / <see cref="GpResult.Failed"/>).</returns>
    Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken);
}
