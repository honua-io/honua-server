// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Geoprocessing.Cli.Publish;

/// <summary>
/// Parsed options for the <c>honua gp publish</c> verb.
/// </summary>
internal sealed record GpPublishOptions
{
    /// <summary>The workflow or process id to publish.</summary>
    public required string Id { get; init; }

    /// <summary>Server root URL. Defaults to the local quickstart (<c>http://localhost:8080</c>).</summary>
    public string Server { get; init; } = "http://localhost:8080";

    /// <summary>Admin API key sent as <c>X-API-Key</c>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Bearer token sent as <c>Authorization: Bearer</c>.</summary>
    public string? BearerToken { get; init; }

    /// <summary>Author message stamped onto the package metadata.</summary>
    public string? Message { get; init; }

    /// <summary>Validate + dry-run only; never create a publication.</summary>
    public bool DryRun { get; init; }

    /// <summary>Also publish the created version to a process-endpoint target.</summary>
    public bool Publish { get; init; }

    /// <summary>Explicit workflow graph JSON file, overriding fixture/process resolution.</summary>
    public string? File { get; init; }

    /// <summary>Wrap a single registered process into a one-node graph.</summary>
    public bool AsProcessNode { get; init; }

    /// <summary>Override the package name (defaults to the id).</summary>
    public string? Name { get; init; }

    /// <summary>Override the package namespace.</summary>
    public string? Namespace { get; init; }

    /// <summary>Fixture root holding <c>&lt;id&gt;/workflow.json</c>. Defaults to <c>samples/gp</c>.</summary>
    public string FixtureRoot { get; init; } = Path.Combine("samples", "gp");
}

/// <summary>
/// Implements the <c>honua gp publish</c> verb: it builds the WorkflowPackage/WorkflowGraph
/// payload a locally-authored workflow maps to and drives the Console workflow-package REST
/// surface (save → version → validate → dry-run → optional publish), mirroring the Console UI.
/// </summary>
/// <remarks>
/// This bridges the devkit's local loop to the GitOps cross-env path: publishing creates an
/// immutable, versioned package in the store and emits a metadata release package for promotion
/// through the GitOps approval path. It does NOT deploy across environments and does NOT auto-promote
/// — promotion is governed/approval-gated (honua-devops is plan + PR-first, submitImmediately=false).
/// </remarks>
internal static class GpPublishCommand
{
    /// <summary>
    /// Parses the publish args, resolves the id to a graph, and drives the REST sequence.
    /// </summary>
    /// <param name="args">The verb's options (everything after <c>publish</c>).</param>
    /// <param name="processIds">Registered process ids, used to detect code processes.</param>
    /// <param name="catalog">Process catalog, used to seed default parameters for process nodes.</param>
    /// <param name="clientFactory">
    /// Factory producing the REST client for an <c>(server, apiKey, bearer)</c> tuple. Injected so
    /// offline tests can supply a faked transport; production uses the default factory.
    /// </param>
    /// <param name="output">Standard output sink.</param>
    /// <param name="error">Error output sink.</param>
    public static async Task<int> RunAsync(
        string[] args,
        IReadOnlySet<string> processIds,
        IProcessCatalog? catalog,
        Func<GpPublishOptions, WorkflowPackagePublishClient> clientFactory,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var options = ParseOptions(args);

        var source = new GpWorkflowSource(catalog, processIds)
            .Resolve(options.Id, options.File, options.AsProcessNode, options.Name, options.FixtureRoot);

        if (!source.CanPublish)
        {
            // Honest non-publish path. A code process is an expected, non-error outcome
            // (exit 0) — it simply deploys via the server image; an unknown id is an error.
            output.WriteLine($"id      : {options.Id}");
            output.WriteLine($"kind    : {source.Kind}");
            output.WriteLine($"result  : not published");
            output.WriteLine($"reason  : {source.Reason}");
            return string.Equals(source.Kind, "code process", StringComparison.Ordinal) ? 0 : 2;
        }

        var graph = source.Graph!;
        var saveRequest = new SaveWorkflowPackageRequestDto
        {
            PackageId = options.Id,
            Name = source.Name ?? options.Id,
            Namespace = options.Namespace,
            Description = options.Message,
            Graph = graph,
            Metadata = BuildMetadata(options, source)
        };

        var client = clientFactory(options);

        GpPublishResult result;
        try
        {
            if (options.DryRun)
            {
                result = await client.DryRunOnlyAsync(saveRequest).ConfigureAwait(false);
            }
            else
            {
                PublishWorkflowPackageRequestDto? publishRequest = options.Publish
                    ? BuildPublishRequest(source)
                    : null;

                result = await client.SaveVersionAndPublishAsync(
                        saveRequest,
                        validate: true,
                        dryRun: false,
                        publish: options.Publish,
                        publishRequest,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (GpPublishHttpException ex)
        {
            error.WriteLine($"error   : {ex.Message}");
            return 1;
        }

        WriteResult(output, options, source, result);
        return 0;
    }

    private static Dictionary<string, string> BuildMetadata(GpPublishOptions options, GpPublishSource source)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["authoredVia"] = "honua-gp-devkit",
            ["sourceKind"] = source.Kind
        };

        if (!string.IsNullOrWhiteSpace(options.Message))
        {
            metadata["message"] = options.Message;
        }

        if (!string.IsNullOrWhiteSpace(source.ProcessId))
        {
            metadata["processId"] = source.ProcessId;
        }

        return metadata;
    }

    private static PublishWorkflowPackageRequestDto BuildPublishRequest(GpPublishSource source)
        => new()
        {
            // The devkit publishes single authored workflows as process-style endpoints — the
            // target a locally-authored process/workflow most naturally maps to.
            Target = WorkflowPublicationTarget.ProcessEndpoint,
            ProcessId = source.ProcessId,
            Enabled = true
        };

    private static void WriteResult(
        TextWriter output,
        GpPublishOptions options,
        GpPublishSource source,
        GpPublishResult result)
    {
        output.WriteLine($"id        : {options.Id}");
        output.WriteLine($"kind      : {source.Kind}");
        output.WriteLine($"server    : {options.Server.TrimEnd('/')}");
        output.WriteLine($"package   : {result.Package.PackageId}");
        output.WriteLine($"nodes     : {source.Graph!.Nodes.Count}");

        if (result.Version is not null)
        {
            output.WriteLine($"version   : {result.Version.Version}");
            output.WriteLine($"hash      : {result.Version.PackageHash}");
        }

        if (result.Validation is not null)
        {
            output.WriteLine($"validate  : {(result.Validation.IsValid ? "valid" : "INVALID")}");
            foreach (var failure in result.Validation.Failures)
            {
                output.WriteLine($"  - {failure.Code}: {failure.Message}");
            }

            foreach (var warning in result.Validation.Warnings)
            {
                output.WriteLine($"  ! {warning}");
            }
        }

        if (result.DryRun is not null)
        {
            output.WriteLine(
                $"dry-run   : ~{result.DryRun.EstimatedDurationSeconds}s, "
                + $"cost {result.DryRun.EstimatedCostWeight:F1}, "
                + $"{result.DryRun.OutputSchemas.Count} output(s)");
        }

        if (result.Publication is not null)
        {
            output.WriteLine($"published : {result.Publication.PublicationId} -> {result.Publication.Target}");
            if (!string.IsNullOrWhiteSpace(result.Publication.EndpointPath))
            {
                output.WriteLine($"endpoint  : {result.Publication.EndpointPath}");
            }
        }

        output.WriteLine();
        if (options.DryRun)
        {
            output.WriteLine(
                "result    : validated + dry-ran a draft version. Nothing was published. "
                + "Re-run without --dry-run to create a publishable version.");
        }
        else if (result.Publication is not null)
        {
            output.WriteLine(
                "result    : package version published. Next: it emits a metadata release package "
                + "for promotion through the GitOps approval path (governed/approval-gated, not "
                + "auto-promoted) (this command only published the package; it does NOT deploy across envs).");
        }
        else
        {
            output.WriteLine(
                "result    : immutable package version created and ready to publish. Next: publish it "
                + "(re-run with --publish, or use the Console) to emit a metadata release package for "
                + "promotion through the GitOps approval path (governed/approval-gated, not auto-promoted) "
                + "(publishing does NOT deploy across envs).");
        }
    }

    /// <summary>
    /// Parses the publish verb's options. Exposed to tests.
    /// </summary>
    internal static GpPublishOptions ParseOptions(string[] args)
    {
        if (args.Length == 0)
        {
            throw new GpCliUsageException(
                "Missing <id>. Usage: honua gp publish <id> [--server <url>] [--api-key <key>] "
                + "[--message <m>] [--dry-run] [--publish] [--as-process-node] [--file <workflow.json>]");
        }

        var id = args[0];
        if (id.StartsWith('-'))
        {
            throw new GpCliUsageException("The first argument to 'publish' must be a workflow or process id.");
        }

        string server = "http://localhost:8080";
        string? apiKey = Environment.GetEnvironmentVariable("HONUA_ADMIN_PASSWORD");
        string? bearer = null;
        string? message = null;
        var dryRun = false;
        var publish = false;
        string? file = null;
        var asProcessNode = false;
        string? name = null;
        string? ns = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server" or "-s":
                    server = NextValue(args, ref i, "--server");
                    break;
                case "--api-key" or "-k":
                    apiKey = NextValue(args, ref i, "--api-key");
                    break;
                case "--bearer" or "--token":
                    bearer = NextValue(args, ref i, "--bearer");
                    break;
                case "--message" or "-m":
                    message = NextValue(args, ref i, "--message");
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--publish":
                    publish = true;
                    break;
                case "--file" or "-f":
                    file = NextValue(args, ref i, "--file");
                    break;
                case "--as-process-node":
                    asProcessNode = true;
                    break;
                case "--name":
                    name = NextValue(args, ref i, "--name");
                    break;
                case "--namespace":
                    ns = NextValue(args, ref i, "--namespace");
                    break;
                default:
                    throw new GpCliUsageException($"Unknown option '{args[i]}'.");
            }
        }

        if (dryRun && publish)
        {
            throw new GpCliUsageException("--dry-run and --publish are mutually exclusive.");
        }

        return new GpPublishOptions
        {
            Id = id,
            Server = server,
            ApiKey = apiKey,
            BearerToken = bearer,
            Message = message,
            DryRun = dryRun,
            Publish = publish,
            File = file,
            AsProcessNode = asProcessNode,
            Name = name,
            Namespace = ns
        };
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new GpCliUsageException($"Option '{option}' requires a value.");
        }

        return args[++index];
    }
}
