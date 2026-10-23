// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Secret/ConfigMap-sourced worker environment for the Kubernetes Job backend (honua-helm#98,
/// owner decision 18): references render into <c>envFrom</c> / <c>valueFrom</c>, launch variables stay
/// literal and protected, and only reference names ever reach the server.
/// </summary>
public sealed class KubernetesJobEnvironmentTests
{
    [UnitTest]
    public void BuildManifest_RendersEnvFromKeyRefsAndLiteralsTogether()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>(), new KubernetesExecutionOptions
        {
            DefaultNamespace = "honua",
            DefaultEnvFromSecrets = ["honua-runtime"],
        });
        var job = CreateJob("job-env-mix", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k8s.env.LOG_LEVEL"] = "debug",
            ["env.SHARED_FLAG"] = "on",
            ["k8s.env.ConnectionStrings__DefaultConnection.secretKeyRef"] = "honua-db:connection-string",
            ["k8s.env.FEATURE_FLAGS.configMapKeyRef"] = "honua-flags:flags.json",
            ["k8s.envFrom.secret.10"] = "extra-secret",
            ["k8s.envFrom.secret.2"] = "honua-redis:REDIS_",
            ["k8s.envFrom.configMap.0"] = "honua-config",
        });

        var manifest = backend.BuildManifest(job, "honua/worker:1");

        manifest.EnvironmentVariables["LOG_LEVEL"].Should().Be("debug");
        manifest.EnvironmentVariables["SHARED_FLAG"].Should().Be("on");
        manifest.EnvironmentVariables.Keys.Should().NotContain(key => key.Contains("KeyRef", StringComparison.Ordinal));
        manifest.EnvironmentReferences["ConnectionStrings__DefaultConnection"].Should()
            .Be(new KubernetesEnvKeyReference(KubernetesEnvSourceKind.Secret, "honua-db", "connection-string"));
        manifest.EnvironmentReferences["FEATURE_FLAGS"].Should()
            .Be(new KubernetesEnvKeyReference(KubernetesEnvSourceKind.ConfigMap, "honua-flags", "flags.json"));
        // Defaults first, then workload entries ordered numerically by <n> ("2" before "10").
        manifest.EnvFrom.Should().Equal(
            new KubernetesEnvFromSource(KubernetesEnvSourceKind.Secret, "honua-runtime"),
            new KubernetesEnvFromSource(KubernetesEnvSourceKind.ConfigMap, "honua-config"),
            new KubernetesEnvFromSource(KubernetesEnvSourceKind.Secret, "honua-redis", "REDIS_"),
            new KubernetesEnvFromSource(KubernetesEnvSourceKind.Secret, "extra-secret"));

        using var document = JsonDocument.Parse(KubernetesJobManifestSerializer.SerializeJobManifest(manifest));
        var container = document.RootElement.GetProperty("spec").GetProperty("template").GetProperty("spec")
            .GetProperty("containers")[0];
        var env = container.GetProperty("env").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("name").GetString()!, entry => entry);

        env["LOG_LEVEL"].GetProperty("value").GetString().Should().Be("debug");
        var secretRef = env["ConnectionStrings__DefaultConnection"];
        secretRef.TryGetProperty("value", out _).Should().BeFalse();
        secretRef.GetProperty("valueFrom").GetProperty("secretKeyRef").GetProperty("name").GetString()
            .Should().Be("honua-db");
        secretRef.GetProperty("valueFrom").GetProperty("secretKeyRef").GetProperty("key").GetString()
            .Should().Be("connection-string");
        var configMapRef = env["FEATURE_FLAGS"].GetProperty("valueFrom").GetProperty("configMapKeyRef");
        configMapRef.GetProperty("name").GetString().Should().Be("honua-flags");
        configMapRef.GetProperty("key").GetString().Should().Be("flags.json");
        env["HONUA_OPERATION_ID"].GetProperty("value").GetString().Should().Be("job-env-mix");

        var envFrom = container.GetProperty("envFrom").EnumerateArray().ToList();
        envFrom.Should().HaveCount(4);
        envFrom[0].GetProperty("secretRef").GetProperty("name").GetString().Should().Be("honua-runtime");
        envFrom[0].TryGetProperty("prefix", out _).Should().BeFalse();
        envFrom[1].GetProperty("configMapRef").GetProperty("name").GetString().Should().Be("honua-config");
        envFrom[2].GetProperty("secretRef").GetProperty("name").GetString().Should().Be("honua-redis");
        envFrom[2].GetProperty("prefix").GetString().Should().Be("REDIS_");
        envFrom[3].GetProperty("secretRef").GetProperty("name").GetString().Should().Be("extra-secret");
    }

    [UnitTest]
    public void BuildManifest_AppliesConfiguredDefaultsWithoutWorkloadParameters()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>(), new KubernetesExecutionOptions
        {
            DefaultNamespace = "honua",
            DefaultEnvFromSecrets = ["honua-runtime", " ", "honua-runtime"],
            DefaultEnvFromConfigMaps = ["honua-settings:HONUA_"],
        });
        var job = CreateJob("job-defaults", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Re-declaring a default per workload adds nothing.
            ["k8s.envFrom.secret.0"] = "honua-runtime",
        });

        var manifest = backend.BuildManifest(job, "honua/worker:1");

        manifest.EnvFrom.Should().Equal(
            new KubernetesEnvFromSource(KubernetesEnvSourceKind.Secret, "honua-runtime"),
            new KubernetesEnvFromSource(KubernetesEnvSourceKind.ConfigMap, "honua-settings", "HONUA_"));
        manifest.EnvironmentReferences.Should().BeEmpty();
    }

    [UnitTest]
    public void BuildManifest_WithoutReferences_EmitsNoEnvFrom()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var manifest = backend.BuildManifest(CreateJob("job-plain", new Dictionary<string, string>()), "honua/worker:1");

        using var document = JsonDocument.Parse(KubernetesJobManifestSerializer.SerializeJobManifest(manifest));
        var container = document.RootElement.GetProperty("spec").GetProperty("template").GetProperty("spec")
            .GetProperty("containers")[0];
        container.TryGetProperty("envFrom", out _).Should().BeFalse();
        container.GetProperty("env").EnumerateArray()
            .Select(entry => entry.TryGetProperty("value", out _))
            .Should().AllSatisfy(hasValue => hasValue.Should().BeTrue());
    }

    [UnitTest]
    public void ControlPlaneOptions_BindsDefaultEnvFromSecretsFromChartEnvironment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // ControlPlane__Kubernetes__DefaultEnvFromSecrets__0=honua-runtime
                [$"{ControlPlaneOptions.SectionName}:Kubernetes:DefaultEnvFromSecrets:0"] = "honua-runtime",
                [$"{ControlPlaneOptions.SectionName}:Kubernetes:DefaultEnvFromConfigMaps:0"] = "honua-settings",
            })
            .Build();
        var options = new ControlPlaneOptions();

        configuration.GetSection(ControlPlaneOptions.SectionName).Bind(options);

        options.Kubernetes.DefaultEnvFromSecrets.Should().Equal("honua-runtime");
        options.Kubernetes.DefaultEnvFromConfigMaps.Should().Equal("honua-settings");
    }

    [UnitTheory]
    [InlineData("HONUA_OPERATION_ID")]
    [InlineData("HONUA_JOB_KIND")]
    [InlineData("HONUA_WORKLOAD_NAME")]
    [InlineData("HONUA_WORKLOAD_ID")]
    [InlineData("HONUA_RUNTIME_PROFILE")]
    [InlineData("HONUA_CONTRACT_VERSION")]
    [InlineData("HONUA_EXECUTION_ATTEMPT")]
    public void BuildManifest_ReservedLaunchVariableCannotBeSecretSourced(string reserved)
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var job = CreateJob("job-reserved", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"k8s.env.{reserved}.secretKeyRef"] = "attacker:value",
        });

        var act = () => backend.BuildManifest(job, "honua/worker:1");

        act.Should().Throw<KubernetesJobParameterException>()
            .Which.Error.Should().Be(KubernetesJobParameterError.ReservedEnvironmentVariable);
    }

    [UnitTest]
    public void BuildManifest_LiteralPassthroughNeverSetsLaunchVariables()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var job = CreateJob("job-literal-reserved", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k8s.env.HONUA_OPERATION_ID"] = "someone-else",
            ["env.HONUA_EXECUTION_ATTEMPT"] = "99",
            // Not stamped by the launch for this job (no runtime profile), so it must not leak through.
            ["k8s.env.HONUA_RUNTIME_PROFILE"] = "native",
        });

        var manifest = backend.BuildManifest(job, "honua/worker:1");

        manifest.EnvironmentVariables["HONUA_OPERATION_ID"].Should().Be("job-literal-reserved");
        manifest.EnvironmentVariables["HONUA_EXECUTION_ATTEMPT"].Should().Be("1");
        manifest.EnvironmentVariables.Should().NotContainKey("HONUA_RUNTIME_PROFILE");
    }

    [UnitTheory]
    [InlineData("k8s.envFrom.secret.0", "Bad_Name", nameof(KubernetesJobParameterError.InvalidObjectName))]
    [InlineData("k8s.envFrom.secret.0", "", nameof(KubernetesJobParameterError.InvalidObjectName))]
    [InlineData("k8s.envFrom.secret.0", "honua-runtime:1BAD", nameof(KubernetesJobParameterError.InvalidEnvFromPrefix))]
    [InlineData("k8s.envFrom.secret.", "honua-runtime", nameof(KubernetesJobParameterError.InvalidEnvFromKey))]
    [InlineData("k8s.envFrom.secret.a/b", "honua-runtime", nameof(KubernetesJobParameterError.InvalidEnvFromKey))]
    [InlineData("k8s.envFrom.secrets.0", "honua-runtime", nameof(KubernetesJobParameterError.InvalidEnvFromKey))]
    [InlineData("k8s.envFrom.configmap.0", "honua-config", nameof(KubernetesJobParameterError.InvalidEnvFromKey))]
    [InlineData("k8s.env.DB.secretKeyRef", "honua-db", nameof(KubernetesJobParameterError.InvalidKeyReference))]
    [InlineData("k8s.env.DB.secretKeyRef", "honua-db:", nameof(KubernetesJobParameterError.InvalidKeyReference))]
    [InlineData("k8s.env.DB.secretKeyRef", "honua-db:a/b", nameof(KubernetesJobParameterError.InvalidKeyReference))]
    [InlineData("k8s.env.DB.secretKeyRef", "Honua-DB:key", nameof(KubernetesJobParameterError.InvalidObjectName))]
    [InlineData("k8s.env.DB.configMapKeyRef", ":key", nameof(KubernetesJobParameterError.InvalidObjectName))]
    [InlineData("k8s.env.1DB.secretKeyRef", "honua-db:key", nameof(KubernetesJobParameterError.InvalidEnvironmentVariableName))]
    [InlineData("k8s.env..secretKeyRef", "honua-db:key", nameof(KubernetesJobParameterError.InvalidEnvironmentVariableName))]
    [InlineData("k8s.env.DB.secretkeyref", "honua-db:key", nameof(KubernetesJobParameterError.UnknownValueSource))]
    public void BuildManifest_InvalidReferenceShapes_AreRefusedWithTypedErrors(
        string key,
        string value,
        string expected)
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var job = CreateJob("job-invalid", new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

        var act = () => backend.BuildManifest(job, "honua/worker:1");

        var thrown = act.Should().Throw<KubernetesJobParameterException>().Which;
        thrown.Error.ToString().Should().Be(expected);
        thrown.ParameterKey.Should().Be(key);
    }

    [UnitTest]
    public void BuildManifest_LiteralAndReferenceForSameVariable_IsRefused()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var job = CreateJob("job-conflict", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k8s.env.DB_PASSWORD"] = "literal",
            ["k8s.env.DB_PASSWORD.secretKeyRef"] = "honua-db:password",
        });

        var act = () => backend.BuildManifest(job, "honua/worker:1");

        act.Should().Throw<KubernetesJobParameterException>()
            .Which.Error.Should().Be(KubernetesJobParameterError.ConflictingEnvironmentVariable);
    }

    [UnitTest]
    public void BuildManifest_TwoReferencesForSameVariable_IsRefused()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var job = CreateJob("job-conflict-refs", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k8s.env.DB_PASSWORD.configMapKeyRef"] = "honua-config:password",
            ["k8s.env.DB_PASSWORD.secretKeyRef"] = "honua-db:password",
        });

        var act = () => backend.BuildManifest(job, "honua/worker:1");

        act.Should().Throw<KubernetesJobParameterException>()
            .Which.Error.Should().Be(KubernetesJobParameterError.ConflictingEnvironmentVariable);
    }

    [UnitTest]
    public void BuildManifest_InvalidConfiguredDefault_IsRefused()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>(), new KubernetesExecutionOptions
        {
            DefaultEnvFromSecrets = ["Not A Secret"],
        });

        var act = () => backend.BuildManifest(CreateJob("job-bad-default", new Dictionary<string, string>()), "honua/worker:1");

        var thrown = act.Should().Throw<KubernetesJobParameterException>().Which;
        thrown.Error.Should().Be(KubernetesJobParameterError.InvalidObjectName);
        thrown.ParameterKey.Should().Be("ControlPlane:Kubernetes:DefaultEnvFromSecrets:0");
    }

    [UnitTest]
    public async Task StartAsync_InvalidReference_FailsBeforeCreateAndNeverEchoesValues()
    {
        const string literalSecret = "s3cr3t-literal-value";
        var client = Substitute.For<IKubernetesJobClient>();
        var logger = new ListLogger<KubernetesJobBatchComputeBackend>();
        var backend = CreateBackend(client, logger: logger);
        var job = CreateJob("job-refused", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k8s.env.API_TOKEN"] = literalSecret,
            ["k8s.env.API_TOKEN.secretKeyRef"] = "honua-api:token",
        });

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("k8s.env.API_TOKEN.secretKeyRef");
        result.Message.Should().NotContain(literalSecret);
        result.ResolvedParameters.Should().BeNull();
        logger.Entries.Should().ContainSingle(entry => entry.Contains("k8s.env.API_TOKEN.secretKeyRef", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(entry => entry.Contains(literalSecret, StringComparison.Ordinal));
        await client.DidNotReceiveWithAnyArgs().CreateJobAsync(default!);
    }

    [UnitTest]
    public async Task StartAsync_WithReferences_PersistsOnlyNamespaceAndSubmitsReferenceNames()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        KubernetesJobManifest? submitted = null;
        client.CreateJobAsync(Arg.Do<KubernetesJobManifest>(m => submitted = m), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobCreateResult
            {
                StatusCode = HttpStatusCode.Created,
                Snapshot = new KubernetesJobStatusSnapshot { Uid = "uid-refs" }
            });
        var logger = new ListLogger<KubernetesJobBatchComputeBackend>();
        var backend = CreateBackend(client, new KubernetesExecutionOptions
        {
            DefaultNamespace = "honua",
            DefaultEnvFromSecrets = ["honua-runtime"],
            DefaultImageMaxSupportedContractVersion = RasterSourceContract.JobContractVersion,
        }, logger);
        var job = CreateJob("job-refs", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k8s.env.ConnectionStrings__DefaultConnection.secretKeyRef"] = "honua-db:connection-string",
        });

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Provisioning);
        // The durable record only gains the pinned namespace — no environment, no references.
        result.ResolvedParameters.Should().NotBeNull();
        result.ResolvedParameters!.Keys.Should().Equal(KubernetesJobParameterKeys.Namespace);
        submitted.Should().NotBeNull();
        submitted!.EnvironmentVariables.Should().NotContainKey("ConnectionStrings__DefaultConnection");
        var body = System.Text.Encoding.UTF8.GetString(KubernetesJobManifestSerializer.SerializeJobManifest(submitted));
        body.Should().Contain("\"secretKeyRef\":{\"name\":\"honua-db\",\"key\":\"connection-string\"}");
        body.Should().Contain("\"envFrom\":[{\"secretRef\":{\"name\":\"honua-runtime\"}}]");
        logger.Entries.Should().BeEmpty();
    }

    [UnitTest]
    public void Validator_RefusesMalformedKubernetesWorkloadReferencesAtStartup()
    {
        var options = new ControlPlaneOptions
        {
            ExecutionWorkloads =
            [
                new ExecutionWorkloadOptions
                {
                    WorkloadId = "gp-k8s",
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = KubernetesJobBatchComputeBackend.BackendId,
                    ParameterEntries =
                    [
                        new ConfigurationParameterEntryOptions
                        {
                            Key = "k8s.env.HONUA_OPERATION_ID.secretKeyRef",
                            Value = "honua-runtime:op"
                        }
                    ]
                },
                // A different backend's parameters are not the Kubernetes adapter's to judge.
                new ExecutionWorkloadOptions
                {
                    WorkloadId = "gp-local",
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["k8s.envFrom.secret.0"] = "Not Valid"
                    }
                }
            ],
            Kubernetes = new KubernetesExecutionOptions { DefaultEnvFromSecrets = ["honua_runtime"] }
        };

        var result = new ControlPlaneOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        var failures = result.Failures!.ToList();
        failures.Should().Contain(f => f.Contains("ExecutionWorkloads:0", StringComparison.Ordinal)
            && f.Contains("HONUA_OPERATION_ID", StringComparison.Ordinal));
        failures.Should().Contain(f => f.Contains("DefaultEnvFromSecrets:0", StringComparison.Ordinal));
        failures.Should().NotContain(f => f.Contains("gp-local", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validator_AcceptsWellFormedKubernetesWorkloadReferences()
    {
        var options = new ControlPlaneOptions
        {
            ExecutionWorkloads =
            [
                new ExecutionWorkloadOptions
                {
                    WorkloadId = "gp-k8s",
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = KubernetesJobBatchComputeBackend.BackendId,
                    ParameterEntries =
                    [
                        new ConfigurationParameterEntryOptions
                        {
                            Key = "k8s.env.ConnectionStrings__DefaultConnection.secretKeyRef",
                            Value = "honua-db:connection-string"
                        },
                        new ConfigurationParameterEntryOptions { Key = "k8s.envFrom.configMap.0", Value = "honua-config" }
                    ]
                }
            ],
            Kubernetes = new KubernetesExecutionOptions { DefaultEnvFromSecrets = ["honua-runtime"] }
        };

        var result = new ControlPlaneOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeTrue(string.Join(" | ", result.Failures ?? []));
    }

    private static KubernetesJobBatchComputeBackend CreateBackend(
        IKubernetesJobClient client,
        KubernetesExecutionOptions? options = null,
        ILogger<KubernetesJobBatchComputeBackend>? logger = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<KubernetesExecutionOptions>>();
        monitor.CurrentValue.Returns(options ?? new KubernetesExecutionOptions
        {
            DefaultNamespace = "default",
            DefaultImageMaxSupportedContractVersion = RasterSourceContract.JobContractVersion,
        });
        return new KubernetesJobBatchComputeBackend(
            client,
            monitor,
            logger ?? new ListLogger<KubernetesJobBatchComputeBackend>());
    }

    private static ExecutionJobRecord CreateJob(string operationId, Dictionary<string, string> parameters)
        => new()
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = KubernetesJobBatchComputeBackend.BackendId,
                WorkloadName = "workload",
                Artifact = "honua/worker:1",
                Parameters = parameters
            }
        };

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }
}
