// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.Hosting;

/// <summary>
/// Ends the process soon after the host starts stopping inside an AWS Lambda sandbox, so Lambda
/// recycles the sandbox instead of routing invocations to a process that has stopped listening.
/// </summary>
/// <remarks>
/// <para>
/// Lambda freezes the sandbox as soon as an invocation returns. A host that begins a graceful
/// stop (for example <c>BackgroundServiceExceptionBehavior.StopHost</c> after a background loop
/// fails) closes Kestrel's port at once, but the rest of the stop only runs during the few
/// milliseconds each later invocation thaws the sandbox. The Lambda Web Adapter answers those
/// invocations with <c>lambda_runtime::layers::panic</c> (connection refused), and Lambda keeps
/// the sandbox because the runtime process has not exited. On the demo cell this lasted about
/// eight minutes per sandbox (honua-server#6558).
/// </para>
/// <para>
/// The guard arms a timer when <see cref="IHostApplicationLifetime.ApplicationStopping"/> fires.
/// The grace lets the in-flight invocation drain. Timers measure elapsed time, which keeps
/// advancing while the sandbox is frozen, so the first thaw after the grace exits the process.
/// Lambda then reports <c>Runtime.ExitError</c> and starts a fresh sandbox.
/// </para>
/// </remarks>
internal sealed partial class LambdaHostStopExitGuard : IHostedService, IDisposable
{
    /// <summary>Set by the AWS Lambda runtime in every function's environment.</summary>
    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>Set by the AWS Lambda runtime for the runtime API endpoint.</summary>
    internal const string AwsLambdaRuntimeApiKey = "AWS_LAMBDA_RUNTIME_API";

    /// <summary>Process status for a stop that had not finished by the end of the grace.</summary>
    internal const int StalledStopExitCode = 1;

    /// <summary>Time the in-flight invocation gets to drain before the process exits.</summary>
    internal static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(10);

    // Roots the armed timer. Host disposal can drop this service while the stop is still stalled,
    // and the exit must still fire.
    private static ITimer? s_armedExit;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LambdaHostStopExitGuard> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _drainGrace;
    private readonly Action<int> _exit;
    private CancellationTokenRegistration _stoppingRegistration;
    private int _armed;

    /// <summary>
    /// Creates the guard used by the serving host.
    /// </summary>
    /// <param name="lifetime">Host lifetime whose stopping signal arms the exit.</param>
    /// <param name="logger">Logger for the exit notice.</param>
    public LambdaHostStopExitGuard(IHostApplicationLifetime lifetime, ILogger<LambdaHostStopExitGuard> logger)
        : this(lifetime, logger, TimeProvider.System, DefaultDrainGrace, Environment.Exit)
    {
    }

    internal LambdaHostStopExitGuard(
        IHostApplicationLifetime lifetime,
        ILogger<LambdaHostStopExitGuard> logger,
        TimeProvider timeProvider,
        TimeSpan drainGrace,
        Action<int> exit)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _drainGrace = drainGrace;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    /// <summary>
    /// True when the process runs inside an AWS Lambda execution environment.
    /// </summary>
    /// <param name="environment">Environment variable accessor.</param>
    /// <returns>Whether the guard applies.</returns>
    internal static bool IsAwsLambda(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !string.IsNullOrWhiteSpace(environment(AwsLambdaFunctionNameKey))
            || !string.IsNullOrWhiteSpace(environment(AwsLambdaRuntimeApiKey));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration = _lifetime.ApplicationStopping.Register(static state => ((LambdaHostStopExitGuard)state!).Arm(), this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Deliberately leaves an armed exit in place: the stop it guards is this one.</remarks>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _stoppingRegistration.Dispose();

    private void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) != 0)
        {
            return;
        }

        LogArmed(_logger, _drainGrace.TotalSeconds);
        s_armedExit = _timeProvider.CreateTimer(
            static state => ((LambdaHostStopExitGuard)state!).ExitStalledProcess(),
            this,
            _drainGrace,
            Timeout.InfiniteTimeSpan);
    }

    private void ExitStalledProcess()
    {
        // Written straight to stderr as well: the logging pipeline may already be shutting down,
        // and this line is the only record of why the sandbox restarted.
        Console.Error.WriteLine(
            "Honua host stop did not finish within the Lambda drain grace; exiting so Lambda recycles the sandbox (honua-server#6558).");
        LogExiting(_logger, _drainGrace.TotalSeconds);
        _exit(StalledStopExitCode);
    }

    [LoggerMessage(EventId = 4149, Level = LogLevel.Warning,
        Message = "Host is stopping inside a Lambda sandbox; the process exits in {GraceSeconds}s unless the stop finishes first")]
    private static partial void LogArmed(ILogger logger, double graceSeconds);

    [LoggerMessage(EventId = 4150, Level = LogLevel.Critical,
        Message = "Host stop did not finish within {GraceSeconds}s inside a Lambda sandbox; exiting the process so Lambda recycles the sandbox")]
    private static partial void LogExiting(ILogger logger, double graceSeconds);
}
