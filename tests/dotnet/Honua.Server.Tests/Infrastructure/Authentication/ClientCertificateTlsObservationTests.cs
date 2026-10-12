// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Security;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.TestKit.Attributes;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Protocol(TestProtocols.Admin)]
public sealed class ClientCertificateTlsObservationTests
{
    [UnitTest]
    public void DefaultValidation_ValidCertificate_RemainsAcceptedWithoutRejectionLog()
    {
        using var certificate = CreateCertificate(expired: false);
        var logger = new RecordingLogger();
        var options = Configure(logger);

        options.ClientCertificateValidation!(certificate, null, SslPolicyErrors.None).Should().BeTrue();
        logger.Events.Should().BeEmpty();
        options.ClientCertificateMode.Should().Be(ClientCertificateMode.AllowCertificate);
    }

    [UnitTest]
    public void DefaultValidation_ExpiredCertificate_RecordsExactLeafAndTimeFailure()
    {
        using var certificate = CreateCertificate(expired: true);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate).Should().BeFalse();
        var logger = new RecordingLogger();
        var options = Configure(logger);

        options.ClientCertificateValidation!(certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeFalse();
        var fields = logger.Events.Should().ContainSingle().Subject;
        fields["LeafSha256"].Should().Be(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        fields["NotBeforeUtc"].Should().Be(certificate.NotBefore.ToUniversalTime());
        fields["NotAfterUtc"].Should().Be(certificate.NotAfter.ToUniversalTime());
        ((X509ChainStatusFlags)fields["ChainStatus"]!).Should().HaveFlag(X509ChainStatusFlags.NotTimeValid);
        fields["Accepted"].Should().Be(false);
        fields.Keys.Should().BeEquivalentTo("LeafSha256", "NotBeforeUtc", "NotAfterUtc",
            "PolicyErrors", "ChainStatus", "Accepted", "{OriginalFormat}");
    }

    [UnitTest]
    public void DefaultValidation_OtherChainFailure_RemainsRejected()
    {
        using var certificate = CreateCertificate(expired: false);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate).Should().BeFalse();
        var logger = new RecordingLogger();

        Configure(logger).ClientCertificateValidation!(certificate, chain,
            SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
        var fields = logger.Events.Should().ContainSingle().Subject;
        ((X509ChainStatusFlags)fields["ChainStatus"]!).Should().HaveFlag(X509ChainStatusFlags.UntrustedRoot);
    }

    [UnitTheory]
    [InlineData(true, SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(false, SslPolicyErrors.None)]
    public void CustomValidation_OriginalDecisionAndArgumentsArePreserved(bool accepted, SslPolicyErrors errors)
    {
        using var certificate = CreateCertificate(expired: false);
        using var chain = new X509Chain();
        var calls = 0;
        var logger = new RecordingLogger();
        var options = new HttpsConnectionAdapterOptions
        {
            ClientCertificateValidation = (actualCertificate, actualChain, actualErrors) =>
            {
                actualCertificate.Should().BeSameAs(certificate);
                actualChain.Should().BeSameAs(chain);
                actualErrors.Should().Be(errors);
                calls++;
                return accepted;
            }
        };
        ClientCertificateTlsObservation.Configure(options, logger);

        options.ClientCertificateValidation!(certificate, chain, errors).Should().Be(accepted);
        calls.Should().Be(1);
        logger.Events.Count.Should().Be(accepted ? 0 : 1);
    }

    [UnitTest]
    public void CustomValidation_ExceptionIsNotReplacedByLoggingOrDefaultDecision()
    {
        using var certificate = CreateCertificate(expired: false);
        var expected = new InvalidOperationException("validator failure");
        var logger = new RecordingLogger();
        var options = new HttpsConnectionAdapterOptions
        {
            ClientCertificateValidation = (_, _, _) => throw expected
        };
        ClientCertificateTlsObservation.Configure(options, logger);

        Action validate = () => options.ClientCertificateValidation!(certificate, null, SslPolicyErrors.None);
        validate.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
        logger.Events.Should().BeEmpty();
    }

    [UnitTest]
    public void LoggingFailure_CannotAcceptRejectedCertificate()
    {
        using var certificate = CreateCertificate(expired: false);
        Configure(new RecordingLogger { ThrowOnLog = true }).ClientCertificateValidation!(
            certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    public void ProductionHost_HttpsDefaultsInstallObserverAndPreserveOptionalCertificateMode()
    {
        var logger = new RecordingLogger();
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:ClientCertificates:Mode", "Optional");
            // Keep the production AddOptions<...>.Configure<ILoggerFactory>
            // composition, without relying on Serilog's process-wide forwarding
            // to a provider while other test hosts are being created/disposed.
            builder.ConfigureTestServices(services => services.Replace(
                ServiceDescriptor.Singleton<ILoggerFactory>(new ObservationLoggerFactory(logger))));
        });
        var options = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        using var serverCertificate = CreateCertificate(expired: false);
        HttpsConnectionAdapterOptions? applied = null;
        // UseHttps applies the production defaults before this endpoint callback.
        // No listener is started and no certificate is installed in a system store.
        options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(https =>
        {
            applied = https;
            https.ServerCertificate = serverCertificate;
        }));
        applied.Should().NotBeNull();
        // ASP.NET Core v10 HttpsConnectionMiddleware.RemoteCertificateValidationCallback
        // returns mode != RequireCertificate for null certificates before this delegate.
        applied!.ClientCertificateMode.Should().Be(ClientCertificateMode.AllowCertificate,
            "Kestrel must continue allowing an absent certificate without invoking validation");
        applied.ClientCertificateValidation.Should().NotBeNull(
            "the actual Program registration must install the observer");
        applied.ClientCertificateValidation!(serverCertificate, null, SslPolicyErrors.None).Should().BeTrue();
        logger.Events.Should().BeEmpty();

        using var expired = CreateCertificate(expired: true);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(expired);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(expired).Should().BeFalse();
        applied.ClientCertificateValidation(expired, chain, SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeFalse();
        var fields = logger.Events.Should().ContainSingle().Subject;
        fields["LeafSha256"].Should().Be(expired.GetCertHashString(HashAlgorithmName.SHA256));
        ((X509ChainStatusFlags)fields["ChainStatus"]!).Should().HaveFlag(X509ChainStatusFlags.NotTimeValid);
    }

    private sealed class ObservationLoggerFactory(RecordingLogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => categoryName == "Honua.ClientCertificateTls"
            ? logger : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private static HttpsConnectionAdapterOptions Configure(ILogger logger)
    {
        var options = new HttpsConnectionAdapterOptions { ClientCertificateMode = ClientCertificateMode.AllowCertificate };
        ClientCertificateTlsObservation.Configure(options, logger);
        return options;
    }

    private static X509Certificate2 CreateCertificate(bool expired)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Must not appear in diagnostics", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-2), expired ? now.AddDays(-1) : now.AddDays(1));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<Dictionary<string, object?>> Events { get; } = [];
        public bool ThrowOnLog { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog)
            {
                throw new InvalidOperationException("sink failure");
            }

            eventId.Id.Should().Be(4147);
            exception.Should().BeNull();
            Events.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(
                static pair => pair.Key, static pair => pair.Value));
        }
    }
}
