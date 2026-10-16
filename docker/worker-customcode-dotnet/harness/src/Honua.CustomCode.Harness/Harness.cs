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

    /// <summary>
    /// Run one custom-code job end-to-end. Every run also writes the
    /// <c>honua.gp.events/v1</c> journal (<c>CUSTOMCODE_EVENTS_PATH</c>, default
    /// <c>&lt;WorkDir&gt;/honua-events.v1.jsonl</c>): the stderr text is unchanged and the
    /// journal is the structured channel beside it, uploaded to <c>output_prefix</c> after
    /// the tool's own artifacts so the server can forward it as bounded job events.
    /// </summary>
    /// <param name="env">The job environment (defaults to the process environment).</param>
    /// <param name="cancellationToken">Cooperative cancellation for the whole run.</param>
    /// <returns>The terminal process exit code.</returns>
    public async Task<int> RunAsync(
        IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken cancellationToken = default)
    {
        using var journal = new EventJournal(
            EventJournal.PathFromEnvironment(env, Path.Join(_options.WorkDir, EventJournal.FileName)),
            EventJournal.AttemptFromEnvironment(env),
            mirror: _options.Logger ?? new HarnessLogger());
        var state = new RunState();
        var exit = ExitHarnessError;
        try
        {
            exit = await RunCoreAsync(journal, state, env, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            exit = ExitCancelled;
        }
        finally
        {
            journal.Terminal(StatusFor(exit), exit, exit == ExitOk ? null : state.Phase);
            if (!UploadEvents(journal, state.Uploader) && exit == ExitOk)
            {
                exit = ExitHarnessError;
            }
        }

        return exit;
    }

    private static string StatusFor(int exit) => exit switch
    {
        ExitOk => "succeeded",
        ExitToolFailed => "failed",
        ExitCancelled => "cancelled",
        _ => "setupFailed",
    };

    /// <summary>Publish required diagnostics; preserve an original process failure if transport also fails.</summary>
    private static bool UploadEvents(EventJournal journal, ArtifactUploader? uploader)
    {
        if (uploader is null)
        {
            return false;
        }

        try
        {
            uploader.Upload([new Artifact(EventJournal.FileName, journal.Path, new FileInfo(journal.Path).Length)]);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[tool] ERROR event journal publication failed: {ex.GetType().Name}");
            return false;
        }
    }

    private sealed class RunState
    {
        public string Phase { get; set; } = "inputs";
        public ArtifactUploader? Uploader { get; set; }
    }

    private async Task<int> RunCoreAsync(
        EventJournal journal,
        RunState state,
        IReadOnlyDictionary<string, string?>? env,
        CancellationToken cancellationToken)
    {
        IGpLogger log = journal;

        // --- 1. Inputs ------------------------------------------------------
        journal.Phase("inputs");
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

        // Establish evidence transport before any source, dependency or user-code phase.
        try
        {
            string? ReadEnvironment(string name) => env is null
                ? Environment.GetEnvironmentVariable(name)
                : env.GetValueOrDefault(name);
            state.Uploader = _options.UploaderFactory is { } factory
                ? factory(spec.OutputPrefix)
                : ArtifactUploader.CreateBroker(spec.BaseUrl, spec.JobToken,
                    ReadEnvironment("HONUA_JOB_ID"), ReadEnvironment("HONUA_JOB_ATTEMPT"));
            var deadline = ReadEnvironment("HONUA_JOB_START_DEADLINE");
            if ((deadline is not null || _options.UploaderFactory is null) &&
                (!long.TryParse(deadline, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var latestStart) ||
                 DateTimeOffset.UtcNow.ToUnixTimeSeconds() > latestStart))
            {
                log.Warn("Custom-code queue-wait deadline is missing, invalid or exceeded; source execution refused.");
                return ExitHarnessError;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warn($"artifact broker setup failed: {ex.GetType().Name}");
            return ExitHarnessError;
        }

        // --- 2. Pinned source ----------------------------------------------
        state.Phase = "source";
        journal.Phase("source");
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
        state.Phase = "build";
        journal.Phase("build");
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
        state.Phase = "client";
        journal.Phase("client");
        object? client;
        try
        {
            client = CredentialSandbox.BuildScopedClient(spec.BaseUrl, spec.JobToken, _options.ClientFactory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        state.Phase = "execute";
        journal.Phase("execute");
        Directory.CreateDirectory(_options.WorkDir);
        var output = new OutputSink(spec.OutputMaxBytes);
        var context = new GpContext(
            spec.Params,
            new Dictionary<string, string>(),
            client,
            output,
            journal,
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
        // No named local: only disposal is needed here (the tool itself reads the
        // client via `context`), and naming the resource variable trips
        // cs/useless-assignment-to-local since it is never read afterward.
        using (client as IDisposable)
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional catch-all: this boundary runs arbitrary user tool code, so any
                // exception type must be caught, logged in full, and mapped to the harness's
                // "tool failed" exit code rather than crash the harness process.
                log.Warn("tool raised an unhandled exception:");
                log.Warn(ex.ToString());
                journal.Failure(ex.GetType().Name, ex.Message, "execute");
                return ExitToolFailed;
            }
        }

        if (!result.Ok)
        {
            log.Warn($"tool reported failure: {result.Message}");
            journal.Failure("GpResult", result.Message ?? "Tool reported failure.", "execute");
            return ExitToolFailed;
        }

        // --- 6. Upload artifacts -------------------------------------------
        state.Phase = "upload";
        journal.Phase("upload");
        try
        {
            foreach (var uploaded in state.Uploader!.Upload(output.Artifacts))
            {
                log.Info($"uploaded {uploaded.Name} -> {uploaded.Uri} ({uploaded.SizeBytes} bytes)");
                journal.Artifact(uploaded.Name, uploaded.SizeBytes, uri: uploaded.Uri);
            }
        }
        catch (OutputSizeExceededException ex)
        {
            log.Warn($"output cap exceeded: {ex.Message}");
            return ExitToolFailed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
