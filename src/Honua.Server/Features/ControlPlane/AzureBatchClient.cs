// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Honua.Server.Features.ControlPlane;

/// <summary>
/// Logical task-level execution state reported by Azure Batch.
/// </summary>
internal enum AzureBatchTaskExecutionState
{
    /// <summary>
    /// The task does not yet exist in the service (404).
    /// </summary>
    NotFound,

    /// <summary>
    /// The task is queued and waiting for a compute node.
    /// </summary>
    Active,

    /// <summary>
    /// The task has been scheduled and is preparing to run (image pull, resource staging).
    /// </summary>
    Preparing,

    /// <summary>
    /// The task is currently running on a compute node.
    /// </summary>
    Running,

    /// <summary>
    /// The task finished with exit code 0.
    /// </summary>
    CompletedSuccess,

    /// <summary>
    /// The task finished with a non-zero exit code or a scheduling/execution failure.
    /// </summary>
    CompletedFailure,

    /// <summary>
    /// The task or job was terminated via an explicit cancellation request.
    /// </summary>
    Cancelled
}

/// <summary>
/// Observed state of an Azure Batch job and its primary task.
/// </summary>
internal sealed record AzureBatchJobState
{
    /// <summary>
    /// Azure Batch job id.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Logical execution state derived from the task state, exit code, and failure info.
    /// </summary>
    public required AzureBatchTaskExecutionState ExecutionState { get; init; }

    /// <summary>
    /// Raw Azure Batch task <c>state</c> string for diagnostics.
    /// </summary>
    public string? RawTaskState { get; init; }

    /// <summary>
    /// Exit code from the task when available.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Human-readable failure message when the task or job failed.
    /// </summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    /// Retry attempts already consumed by the Azure Batch scheduler.
    /// </summary>
    public int? RetryCount { get; init; }
}

/// <summary>
/// Pool allocation snapshot used to validate that a pool can host a submitted task.
/// </summary>
internal sealed record AzureBatchPoolState
{
    /// <summary>
    /// Azure Batch pool id.
    /// </summary>
    public required string PoolId { get; init; }

    /// <summary>
    /// Pool <c>state</c> value: active, deleting, upgrading.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Pool allocation <c>allocationState</c>: steady, resizing, stopping.
    /// </summary>
    public string? AllocationState { get; init; }

    /// <summary>
    /// Current count of dedicated nodes in the pool.
    /// </summary>
    public int DedicatedNodes { get; init; }

    /// <summary>
    /// Current count of low-priority nodes in the pool.
    /// </summary>
    public int LowPriorityNodes { get; init; }

    /// <summary>
    /// Target dedicated node count (non-zero when the pool is auto-scaling up).
    /// </summary>
    public int TargetDedicatedNodes { get; init; }

    /// <summary>
    /// Target low-priority node count.
    /// </summary>
    public int TargetLowPriorityNodes { get; init; }
}

/// <summary>
/// Request to create an Azure Batch job and single primary task.
/// </summary>
internal sealed record AzureBatchJobSubmission
{
    /// <summary>
    /// Azure Batch account data-plane endpoint, e.g. <c>https://myacct.eastus.batch.azure.com</c>.
    /// </summary>
    public required string AccountUrl { get; init; }

    /// <summary>
    /// Deterministic job id (used as the primary task id as well).
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Target pool id.
    /// </summary>
    public required string PoolId { get; init; }

    /// <summary>
    /// Task command line executed inside the container.
    /// </summary>
    public required string CommandLine { get; init; }

    /// <summary>
    /// Container image reference applied via <c>containerSettings.imageName</c>.
    /// </summary>
    public string? ContainerImage { get; init; }

    /// <summary>
    /// Extra container run options (mounts, env, ports) passed verbatim.
    /// </summary>
    public string? ContainerRunOptions { get; init; }

    /// <summary>
    /// Maximum retry attempts for transient failures at the Azure Batch scheduler layer.
    /// </summary>
    public int MaxTaskRetryCount { get; init; } = 2;

    /// <summary>
    /// Task wall-clock execution timeout.
    /// </summary>
    public TimeSpan? TaskTimeout { get; init; }

    /// <summary>
    /// Blob container shared access URL used as the destination for stdout/stderr uploads.
    /// </summary>
    public string? OutputContainerUrl { get; init; }

    /// <summary>
    /// Environment variables forwarded to the task.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentSettings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Azure Batch data-plane client covering the subset of operations required by the
/// canonical execution-job adapter.
/// </summary>
internal interface IAzureBatchClient
{
    /// <summary>
    /// Creates a job and a single primary task with the same id as the job. Returns
    /// <see cref="HttpStatusCode.Conflict"/> when the job already exists (idempotent).
    /// </summary>
    Task<HttpStatusCode> CreateJobAsync(
        AzureBatchJobSubmission submission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current job + primary task state, collapsing Azure Batch's per-task
    /// state machine into <see cref="AzureBatchTaskExecutionState"/>.
    /// </summary>
    Task<AzureBatchJobState> GetJobStateAsync(
        string accountUrl,
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminates the Azure Batch job (and the primary task) as a cancellation signal.
    /// </summary>
    Task TerminateJobAsync(
        string accountUrl,
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the pool allocation state so callers can validate that tasks will be scheduled.
    /// </summary>
    Task<AzureBatchPoolState> GetPoolStateAsync(
        string accountUrl,
        string poolId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Data-plane REST client for Azure Batch. Uses <see cref="DefaultAzureCredential"/>
/// with the <c>https://batch.core.windows.net/.default</c> scope to mint a dedicated
/// data-plane token per request, following the AzureContainerAppsRevisionClient pattern.
/// </summary>
internal sealed partial class AzureBatchDataPlaneClient : IAzureBatchClient
{
    public const string HttpClientName = "control-plane-azure-batch";

    private const string ApiVersion = "2024-07-01.20.0";
    private static readonly TimeSpan IncompleteJobCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly Uri DataPlaneScope = new("https://batch.core.windows.net/.default");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureBatchDataPlaneClient> _logger;
    private readonly TokenCredential _credential;

    public AzureBatchDataPlaneClient(
        IHttpClientFactory httpClientFactory,
        ILogger<AzureBatchDataPlaneClient> logger)
        : this(httpClientFactory, logger, new DefaultAzureCredential())
    {
    }

    internal AzureBatchDataPlaneClient(
        IHttpClientFactory httpClientFactory,
        ILogger<AzureBatchDataPlaneClient> logger,
        TokenCredential credential)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _credential = credential;
    }

    public async Task<HttpStatusCode> CreateJobAsync(
        AzureBatchJobSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var jobPayload = BuildCreateJobPayload(submission);
        using var jobRequest = await CreateRequestAsync(
                HttpMethod.Post,
                BuildJobsUrl(submission.AccountUrl),
                jobPayload,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var jobResponse = await client.SendAsync(jobRequest, cancellationToken).ConfigureAwait(false);

        // A prior attempt may have created the job but crashed before the task POST; proceed
        // to task creation so the reconciler does not observe perpetual 404s on the task.
        var jobAlreadyExists = jobResponse.StatusCode == HttpStatusCode.Conflict;
        if (jobAlreadyExists)
        {
            Log.JobAlreadyExists(_logger, submission.JobId);
        }
        else
        {
            await EnsureSuccessAsync(jobResponse, cancellationToken).ConfigureAwait(false);
        }

        var cleanupAttempted = false;
        HttpStatusCode taskStatus;
        try
        {
            // Build the task request inside the guarded block so a credential or
            // request-construction failure between the job POST and the task POST still
            // triggers TryDeleteIncompleteJobAsync; otherwise the freshly created job
            // would leak at the provider.
            var taskPayload = BuildCreateTaskPayload(submission);
            using var taskRequest = await CreateRequestAsync(
                    HttpMethod.Post,
                    BuildTasksUrl(submission.AccountUrl, submission.JobId),
                    taskPayload,
                    cancellationToken)
                .ConfigureAwait(false);

            using var taskResponse = await client.SendAsync(taskRequest, cancellationToken).ConfigureAwait(false);
            taskStatus = taskResponse.StatusCode;
            if (taskResponse.StatusCode == HttpStatusCode.Conflict)
            {
                Log.TaskAlreadyExists(_logger, submission.JobId);
                return HttpStatusCode.Conflict;
            }

            if (!taskResponse.IsSuccessStatusCode)
            {
                if (!jobAlreadyExists)
                {
                    cleanupAttempted = true;
                    await TryDeleteIncompleteJobAsync(client, submission.AccountUrl, submission.JobId).ConfigureAwait(false);
                }

                await EnsureSuccessAsync(taskResponse, cancellationToken).ConfigureAwait(false);
            }
        }
        catch when (!jobAlreadyExists && !cleanupAttempted)
        {
            await TryDeleteIncompleteJobAsync(client, submission.AccountUrl, submission.JobId).ConfigureAwait(false);
            throw;
        }

        Log.JobSubmitted(_logger, submission.JobId, submission.PoolId);
        return jobAlreadyExists ? HttpStatusCode.Conflict : taskStatus;
    }

    public async Task<AzureBatchJobState> GetJobStateAsync(
        string accountUrl,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
                HttpMethod.Get,
                BuildTaskUrl(accountUrl, jobId, jobId),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new AzureBatchJobState
            {
                JobId = jobId,
                ExecutionState = AzureBatchTaskExecutionState.NotFound
            };
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseTaskState(jobId, document.RootElement);
    }

    public async Task TerminateJobAsync(
        string accountUrl,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
                HttpMethod.Post,
                BuildJobTerminateUrl(accountUrl, jobId),
                BuildTerminateJobPayload(),
                cancellationToken)
            .ConfigureAwait(false);

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // A job that is already terminal (completed/deleted) returns 404 or 409;
        // treat both as no-op successes because the desired post-state is satisfied.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            Log.TerminateNoOp(_logger, jobId, (int)response.StatusCode);
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        Log.JobTerminated(_logger, jobId);
    }

    public async Task<AzureBatchPoolState> GetPoolStateAsync(
        string accountUrl,
        string poolId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
                HttpMethod.Get,
                BuildPoolUrl(accountUrl, poolId),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParsePoolState(poolId, document.RootElement);
    }

    private async Task TryDeleteIncompleteJobAsync(HttpClient client, string accountUrl, string jobId)
    {
        using var cleanupCancellation = new CancellationTokenSource(IncompleteJobCleanupTimeout);

        try
        {
            using var request = await CreateRequestAsync(
                    HttpMethod.Delete,
                    BuildJobUrl(accountUrl, jobId),
                    null,
                    cleanupCancellation.Token)
                .ConfigureAwait(false);

            using var response = await client.SendAsync(request, cleanupCancellation.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            {
                Log.IncompleteJobCleanupNoOp(_logger, jobId, (int)response.StatusCode);
                return;
            }

            await EnsureSuccessAsync(response, cleanupCancellation.Token).ConfigureAwait(false);
            Log.IncompleteJobCleanupSucceeded(_logger, jobId);
        }
        catch (Exception ex)
        {
            Log.IncompleteJobCleanupFailed(_logger, jobId, ex.Message);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string url,
        byte[]? payload,
        CancellationToken cancellationToken)
    {
        AccessToken accessToken;
        try
        {
            accessToken = await _credential.GetTokenAsync(
                    new TokenRequestContext([DataPlaneScope.ToString()]),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AuthenticationFailedException ex)
        {
            // Normalize credential failures to HttpRequestException with no status code so
            // they flow through the same recoverable path as transport failures. The backend
            // treats null-status HttpRequestException as an ambiguous submission outcome and
            // preserves durable state on observe/cancel instead of promoting the job to
            // Failed while Azure Batch may still be hosting it.
            throw new HttpRequestException(
                $"Azure Batch credential acquisition failed: {ex.Message}",
                ex);
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        if (payload != null)
        {
            request.Content = new ByteArrayContent(payload);
            // Azure Batch requires the odata=minimalmetadata parameter on the Content-Type;
            // MediaTypeHeaderValue only accepts the bare media type via its constructor,
            // so parameters must be added separately.
            var contentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = Encoding.UTF8.WebName
            };
            contentType.Parameters.Add(new NameValueHeaderValue("odata", "minimalmetadata"));
            request.Content.Headers.ContentType = contentType;
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Azure Batch data-plane request failed with status {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }

    internal static AzureBatchJobState ParseTaskState(string jobId, JsonElement root)
    {
        var rawState = root.TryGetProperty("state", out var stateNode) ? stateNode.GetString() : null;
        int? exitCode = null;
        int? retryCount = null;
        string? failureMessage = null;

        if (root.TryGetProperty("executionInfo", out var executionInfo) && executionInfo.ValueKind == JsonValueKind.Object)
        {
            if (executionInfo.TryGetProperty("exitCode", out var exitCodeNode) && exitCodeNode.ValueKind == JsonValueKind.Number)
            {
                exitCode = exitCodeNode.GetInt32();
            }

            if (executionInfo.TryGetProperty("retryCount", out var retryCountNode) && retryCountNode.ValueKind == JsonValueKind.Number)
            {
                retryCount = retryCountNode.GetInt32();
            }

            if (executionInfo.TryGetProperty("failureInfo", out var failureInfo) && failureInfo.ValueKind == JsonValueKind.Object)
            {
                failureMessage = failureInfo.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : null;
            }

            if (executionInfo.TryGetProperty("result", out var resultNode))
            {
                var result = resultNode.GetString();
                if (string.Equals(result, "failure", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(failureMessage))
                {
                    failureMessage = "Azure Batch task completed with failure result.";
                }
            }
        }

        var execution = MapExecutionState(rawState, exitCode, failureMessage);
        return new AzureBatchJobState
        {
            JobId = jobId,
            ExecutionState = execution,
            RawTaskState = rawState,
            ExitCode = exitCode,
            FailureMessage = failureMessage,
            RetryCount = retryCount
        };
    }

    internal static AzureBatchTaskExecutionState MapExecutionState(string? rawState, int? exitCode, string? failureMessage)
    {
        if (string.IsNullOrWhiteSpace(rawState))
        {
            return AzureBatchTaskExecutionState.Active;
        }

        return rawState.Trim().ToLowerInvariant() switch
        {
            "active" => AzureBatchTaskExecutionState.Active,
            "preparing" => AzureBatchTaskExecutionState.Preparing,
            "running" => AzureBatchTaskExecutionState.Running,
            "completed" => exitCode is 0 && string.IsNullOrWhiteSpace(failureMessage)
                ? AzureBatchTaskExecutionState.CompletedSuccess
                : AzureBatchTaskExecutionState.CompletedFailure,
            _ => AzureBatchTaskExecutionState.Active
        };
    }

    private static AzureBatchPoolState ParsePoolState(string poolId, JsonElement root)
        => new()
        {
            PoolId = poolId,
            State = root.TryGetProperty("state", out var state) ? state.GetString() : null,
            AllocationState = root.TryGetProperty("allocationState", out var allocationState) ? allocationState.GetString() : null,
            DedicatedNodes = root.TryGetProperty("currentDedicatedNodes", out var dedicated) && dedicated.ValueKind == JsonValueKind.Number
                ? dedicated.GetInt32()
                : 0,
            LowPriorityNodes = root.TryGetProperty("currentLowPriorityNodes", out var lowPriority) && lowPriority.ValueKind == JsonValueKind.Number
                ? lowPriority.GetInt32()
                : 0,
            TargetDedicatedNodes = root.TryGetProperty("targetDedicatedNodes", out var targetDedicated) && targetDedicated.ValueKind == JsonValueKind.Number
                ? targetDedicated.GetInt32()
                : 0,
            TargetLowPriorityNodes = root.TryGetProperty("targetLowPriorityNodes", out var targetLowPriority) && targetLowPriority.ValueKind == JsonValueKind.Number
                ? targetLowPriority.GetInt32()
                : 0
        };

    internal static byte[] BuildCreateJobPayload(AzureBatchJobSubmission submission)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("id", submission.JobId);
        writer.WriteString("onAllTasksComplete", "terminatejob");
        writer.WriteString("onTaskFailure", "performexitoptionsjobaction");
        writer.WriteStartObject("poolInfo");
        writer.WriteString("poolId", submission.PoolId);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] BuildCreateTaskPayload(AzureBatchJobSubmission submission)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("id", submission.JobId);
        writer.WriteString("commandLine", submission.CommandLine);

        if (!string.IsNullOrWhiteSpace(submission.ContainerImage))
        {
            writer.WriteStartObject("containerSettings");
            writer.WriteString("imageName", submission.ContainerImage);
            if (!string.IsNullOrWhiteSpace(submission.ContainerRunOptions))
            {
                writer.WriteString("containerRunOptions", submission.ContainerRunOptions);
            }

            writer.WriteEndObject();
        }

        writer.WriteStartObject("constraints");
        writer.WriteNumber("maxTaskRetryCount", Math.Max(0, submission.MaxTaskRetryCount));
        if (submission.TaskTimeout.HasValue && submission.TaskTimeout.Value > TimeSpan.Zero)
        {
            writer.WriteString("maxWallClockTime", System.Xml.XmlConvert.ToString(submission.TaskTimeout.Value));
        }

        writer.WriteEndObject();

        if (submission.EnvironmentSettings.Count > 0)
        {
            writer.WriteStartArray("environmentSettings");
            foreach (var (name, value) in submission.EnvironmentSettings)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (!string.IsNullOrWhiteSpace(submission.OutputContainerUrl))
        {
            // Azure Batch wildcard uploads prepend `path` to each matched blob name. A
            // container-level SAS shared across jobs means literal prefixes like "stdlogs"
            // or "artifacts" would collide on the common stdout.txt/stderr.txt names across
            // jobs. Namespace by JobId to keep uploads disambiguated per task.
            writer.WriteStartArray("outputFiles");
            WriteOutputFileEntry(writer, "../std*.txt", submission.OutputContainerUrl!, $"{submission.JobId}/stdlogs");
            WriteOutputFileEntry(writer, "$AZ_BATCH_TASK_DIR/wd/**/*", submission.OutputContainerUrl!, $"{submission.JobId}/artifacts");
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteOutputFileEntry(Utf8JsonWriter writer, string filePattern, string containerUrl, string pathPrefix)
    {
        writer.WriteStartObject();
        writer.WriteString("filePattern", filePattern);
        writer.WriteStartObject("destination");
        writer.WriteStartObject("container");
        writer.WriteString("containerUrl", containerUrl);
        writer.WriteString("path", pathPrefix);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("uploadOptions");
        writer.WriteString("uploadCondition", "taskcompletion");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static byte[] BuildTerminateJobPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("terminateReason", "canonical-runtime-cancellation");
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    private static string NormalizeAccountUrl(string accountUrl)
    {
        var trimmed = (accountUrl ?? string.Empty).Trim();
        return trimmed.EndsWith('/') ? trimmed[..^1] : trimmed;
    }

    private static string BuildJobsUrl(string accountUrl)
        => $"{NormalizeAccountUrl(accountUrl)}/jobs?api-version={ApiVersion}";

    private static string BuildTasksUrl(string accountUrl, string jobId)
        => $"{NormalizeAccountUrl(accountUrl)}/jobs/{Uri.EscapeDataString(jobId)}/tasks?api-version={ApiVersion}";

    private static string BuildTaskUrl(string accountUrl, string jobId, string taskId)
        => $"{NormalizeAccountUrl(accountUrl)}/jobs/{Uri.EscapeDataString(jobId)}/tasks/{Uri.EscapeDataString(taskId)}?api-version={ApiVersion}";

    private static string BuildJobTerminateUrl(string accountUrl, string jobId)
        => $"{NormalizeAccountUrl(accountUrl)}/jobs/{Uri.EscapeDataString(jobId)}/terminate?api-version={ApiVersion}";

    private static string BuildJobUrl(string accountUrl, string jobId)
        => $"{NormalizeAccountUrl(accountUrl)}/jobs/{Uri.EscapeDataString(jobId)}?api-version={ApiVersion}";

    private static string BuildPoolUrl(string accountUrl, string poolId)
        => $"{NormalizeAccountUrl(accountUrl)}/pools/{Uri.EscapeDataString(poolId)}?api-version={ApiVersion}";

    private static partial class Log
    {
        [LoggerMessage(9070, LogLevel.Information, "Azure Batch job {JobId} submitted to pool {PoolId}")]
        public static partial void JobSubmitted(ILogger logger, string jobId, string poolId);

        [LoggerMessage(9071, LogLevel.Information, "Azure Batch job {JobId} already exists; treating as idempotent success")]
        public static partial void JobAlreadyExists(ILogger logger, string jobId);

        [LoggerMessage(9072, LogLevel.Information, "Azure Batch task for job {JobId} already exists; treating as idempotent success")]
        public static partial void TaskAlreadyExists(ILogger logger, string jobId);

        [LoggerMessage(9073, LogLevel.Information, "Azure Batch job {JobId} terminated")]
        public static partial void JobTerminated(ILogger logger, string jobId);

        [LoggerMessage(9074, LogLevel.Debug, "Azure Batch terminate for job {JobId} is a no-op (status {StatusCode})")]
        public static partial void TerminateNoOp(ILogger logger, string jobId, int statusCode);

        [LoggerMessage(9075, LogLevel.Information, "Deleted incomplete Azure Batch job {JobId} after task creation failed")]
        public static partial void IncompleteJobCleanupSucceeded(ILogger logger, string jobId);

        [LoggerMessage(9076, LogLevel.Debug, "Incomplete Azure Batch job cleanup for {JobId} is a no-op (status {StatusCode})")]
        public static partial void IncompleteJobCleanupNoOp(ILogger logger, string jobId, int statusCode);

        [LoggerMessage(9077, LogLevel.Warning, "Failed to delete incomplete Azure Batch job {JobId} after task creation failed: {ErrorMessage}")]
        public static partial void IncompleteJobCleanupFailed(ILogger logger, string jobId, string errorMessage);
    }
}
