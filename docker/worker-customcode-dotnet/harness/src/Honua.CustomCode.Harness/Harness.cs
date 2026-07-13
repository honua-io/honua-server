// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.CustomCode.Sdk;

namespace Honua.CustomCode.Harness;

/// <summary>
/// The .NET custom-code harness orchestrator. Runs one job end-to-end and returns a
/// process exit code. This is the symmetric counterpart of the Python harness's
/// <c>run()</c> — same steps, same exit-code contract:
/// <list type="bullet">
/// <item>0 = succeeded</item>
/// <item>1 = tool returned/raised failure</item>
/// <item>2 = harness/setup error (bad inputs, clone/verify/build failure)</item>
/// <item>3 = job cancelled</item>
/// </list>
/// Most collaborators are injectable so the whole flow is testable offline.
/// </summary>
public sealed class Harness
{
    /// <summary>Exit code: the tool succeeded.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: the tool returned or raised a failure.</summary>
    public const int ExitToolFailed = 1;

    /// <summary>Exit code: a harness/setup error (bad inputs, clone/verify/build failure).</summary>
    public const int ExitHarnessError = 2;

    /// <summary>Exit code: the job was cancelled.</summary>
    public const int ExitCancelled = 3;

    private readonly HarnessOptions _options;

    /// <summary>Creates a harness with the given (mostly injectable) options.</summary>
    /// <param name="options">The harness collaborators + directories.</param>
    public Harness(HarnessOptions? options = null)
    {
        _options = options ?? new HarnessOptions();
    }

    /// <summary>Run one custom-code job end-to-end.</summary>
    /// <param name="env">The job environment (defaults to the process environment).</param>
    /// <param name="cancellationToken">Cooperative cancellation for the whole run.</param>
    /// <returns>The terminal process exit code.</returns>
    public async Task<int> RunAsync(
        IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken cancellationToken = default)
    {
        var log = _options.Logger ?? new HarnessLogger();

        // --- 1. Inputs ------------------------------------------------------
        JobSpec spec;
        try
        {
            spec = JobSpec.Load(env);
        }
        catch (JobSpecException ex)
        {
            log.Warn($"invalid job inputs: {ex.Message}");
            return ExitHarnessError;
        }

        // --- 2. Pinned source ----------------------------------------------
        try
        {
            log.Info($"cloning {spec.RepoUrl} @ {spec.GitRef}");
            _options.SourceFetch.ClonePinned(spec.RepoUrl, spec.GitRef, _options.SourceRoot);
        }
        catch (SourceFetchException ex)
        {
            log.Warn($"source fetch failed: {ex.Message}");
            return ExitHarnessError;
        }

        // --- 3. Build the user project (NTS + SDK already warm-cached) ------
        UserBuildResult build;
        try
        {
            log.Info($"building {spec.DepsManifest}");
            build = _options.ProjectBuilder.Build(_options.SourceRoot, spec.DepsManifest, _options.BuildOutput);
        }
        catch (UserBuildException ex)
        {
            log.Warn($"user project build failed: {ex.Message}");
            return ExitHarnessError;
        }

        // --- 4. Scoped client, THEN scrub credentials ----------------------
        object? client;
        try
        {
            client = CredentialSandbox.BuildScopedClient(spec.BaseUrl, spec.JobToken, _options.ClientFactory);
        }
        catch (Exception ex)
        {
            // Intentional catch-all: the injected client factory is arbitrary (tests
            // supply fakes, production wires a real HTTP client) and any construction
            // failure must degrade to a harness-error exit rather than crash the process.
            log.Warn($"failed to construct scoped client: {ex.Message}");
            return ExitHarnessError;
        }

        if (_options.StripEnvironment)
        {
            var removed = CredentialSandbox.StripCredentialEnv(_options.MutableEnv);
            log.Info($"stripped credential env before user code: {string.Join(", ", removed)}");
            // Invariant: nothing sensitive may remain visible to the tool.
            CredentialSandbox.AssertCredentialsStripped(_options.MutableEnv);
        }

        // --- 5. Load + activate the entrypoint, then run -------------------
        Directory.CreateDirectory(_options.WorkDir);
        var output = new OutputSink(spec.OutputMaxBytes);
        var context = new GpContext(
            spec.Params,
            new Dictionary<string, string>(),
            client,
            output,
            new LoggingProgressReporter(log),
            log,
            _options.WorkDir);

        IGeoprocessingTool tool;
        try
        {
            tool = _options.EntrypointLoader.Load(build.OutputDirectory, spec.Entrypoint);
        }
        catch (EntrypointException ex)
        {
            log.Warn($"entrypoint load failed: {ex.Message}");
            return ExitHarnessError;
        }

        // Scope the scoped-client disposal to the tool run itself: the credential-bearing
        // client must be released as soon as the tool finishes, not linger through the
        // artifact-upload step below.
        GpResult result;
        using (var disposableClient = client as IDisposable)
        {
            try
            {
                result = await tool.ExecuteAsync(context, cancellationToken).ConfigureAwait(false)
                         ?? GpResult.Succeeded();
            }
            catch (OperationCanceledException)
            {
                log.Warn("job cancelled");
                return ExitCancelled;
            }
            catch (Exception ex)
            {
                // Intentional catch-all: this boundary runs arbitrary user tool code, so any
                // exception type must be caught, logged in full, and mapped to the harness's
                // "tool failed" exit code rather than crash the harness process.
                log.Warn("tool raised an unhandled exception:");
                log.Warn(ex.ToString());
                return ExitToolFailed;
            }
        }

        if (!result.Ok)
        {
            log.Warn($"tool reported failure: {result.Message}");
            return ExitToolFailed;
        }

        // --- 6. Upload artifacts -------------------------------------------
        try
        {
            var uploader = _options.UploaderFactory is { } factory
                ? factory(spec.OutputPrefix)
                : new ArtifactUploader(spec.OutputPrefix);
            foreach (var uploaded in uploader.Upload(output.Artifacts))
            {
                log.Info($"uploaded {uploaded.Name} -> {uploaded.Uri} ({uploaded.SizeBytes} bytes)");
            }
        }
        catch (OutputSizeExceededException ex)
        {
            log.Warn($"output cap exceeded: {ex.Message}");
            return ExitToolFailed;
        }
        catch (Exception ex)
        {
            // Intentional catch-all: the uploader is injectable (S3 in production, a fake
            // in tests) and any upload failure must degrade to a harness-error exit rather
            // than crash the process after the tool has already succeeded.
            log.Warn($"artifact upload failed: {ex.Message}");
            return ExitHarnessError;
        }

        log.Info($"job succeeded: {result.Message ?? "ok"}");
        return ExitOk;
    }
}

/// <summary>
/// The injectable collaborators + directories for a <see cref="Harness"/> run. In
/// production the defaults wire to git, the .NET SDK, the real scoped Honua client, and
/// S3; tests inject fakes for every seam.
/// </summary>
public sealed class HarnessOptions
{
    /// <summary>The directory the user source is cloned into.</summary>
    public string SourceRoot { get; set; } = "/work/src";

    /// <summary>The directory the user project is built into.</summary>
    public string BuildOutput { get; set; } = "/work/build";

    /// <summary>The writable scratch directory handed to the tool.</summary>
    public string WorkDir { get; set; } = "/work/out";

    /// <summary>The git clone/verify collaborator.</summary>
    public SourceFetch SourceFetch { get; set; } = new();

    /// <summary>The user-project restore/build collaborator.</summary>
    public UserProjectBuilder ProjectBuilder { get; set; } = new();

    /// <summary>The entrypoint type loader/activator.</summary>
    public EntrypointLoader EntrypointLoader { get; set; } = new();

    /// <summary>An optional scoped-client factory override (tests inject a fake).</summary>
    public Func<string, string, object>? ClientFactory { get; set; }

    /// <summary>An optional uploader factory override (tests inject a fake).</summary>
    public Func<string, ArtifactUploader>? UploaderFactory { get; set; }

    /// <summary>An optional logger override.</summary>
    public IGpLogger? Logger { get; set; }

    /// <summary>Whether to strip the credential environment before user code (default true).</summary>
    public bool StripEnvironment { get; set; } = true;

    /// <summary>
    /// An optional in-memory environment the strip step operates on (tests). When null,
    /// the strip step mutates the real process environment.
    /// </summary>
    public IDictionary<string, string?>? MutableEnv { get; set; }
}
