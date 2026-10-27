// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing.Diagnostics;

namespace Honua.Geoprocessing.UnitTests.Features.Geoprocessing.Diagnostics;

/// <summary>
/// Diagnostic-bundle v1 documents from before the GP attachment still load, secrets do not
/// survive into the attachment, and input bytes are refused unless an export was authorized.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class GpDiagnosticBundleAttachmentTests
{
    private const string Secret = "super-secret";

    private const string PostgresSecret = "s3cret-token";

    private const string AwsKey = "AKIAIOSFODNN7EXAMPLE";

    private const string Hunter = "hunter2";

    private const string MinimalBundle = """
        {
          "schemaVersion": "1.0",
          "contentClassification": "internal",
          "consent": {
            "redactionAcknowledged": true,
            "shareWithSupport": true
          },
          "envelopes": [
            {
              "method": "GET",
              "normalizedPath": "/rest/services/{service}/FeatureServer/{layer}/query"
            }
          ]
        }
        """;

    private const string SanitizedExchangeBundle = """
        {
          "schemaVersion": "1.0",
          "bundleId": "doctor-01J00000000000000000000000",
          "contentClassification": "customer-data",
          "consent": {
            "redactionAcknowledged": true,
            "shareWithSupport": true,
            "grantedBy": "customer-operator"
          },
          "envelopes": [
            {
              "method": "POST",
              "normalizedPath": "/api/v1/tickets/{ticketId}/diagnostics",
              "statusCode": 400,
              "mediaType": "application/problem+json",
              "correlationId": "corr-redacted-01",
              "traceId": "0123456789abcdef0123456789abcdef",
              "capturedAt": "2026-07-11T12:00:00Z",
              "requestHeaders": [
                { "name": "Content-Type", "value": "application/json" }
              ],
              "requestBody": {
                "preview": "{\"query\":\"[REDACTED]\"}",
                "contentSha256": "0000000000000000000000000000000000000000000000000000000000000000",
                "originalByteSize": 128,
                "redactionApplied": true,
                "truncated": false
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parse_MinimalV1Bundle_LoadsWithoutEvidence()
    {
        var document = GpDiagnosticBundleDocument.Parse(MinimalBundle);

        document.SchemaVersion.Should().Be("1.0");
        document.Evidence.Should().BeNull();
        using var json = JsonDocument.Parse(document.ToJson());
        json.RootElement.TryGetProperty("gpEvidence", out _).Should().BeFalse();
        json.RootElement.GetProperty("envelopes")[0].GetProperty("normalizedPath").GetString()
            .Should().Be("/rest/services/{service}/FeatureServer/{layer}/query");
        json.RootElement.GetProperty("consent").GetProperty("redactionAcknowledged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Parse_SanitizedExchangeBundle_LoadsWithoutEvidence()
    {
        var document = GpDiagnosticBundleDocument.Parse(SanitizedExchangeBundle);

        document.Evidence.Should().BeNull();
        using var json = JsonDocument.Parse(document.ToJson());
        json.RootElement.GetProperty("bundleId").GetString().Should().Be("doctor-01J00000000000000000000000");
        json.RootElement.GetProperty("contentClassification").GetString().Should().Be("customer-data");
        json.RootElement.GetProperty("consent").GetProperty("grantedBy").GetString().Should().Be("customer-operator");
        var exchange = json.RootElement.GetProperty("envelopes")[0];
        exchange.GetProperty("statusCode").GetInt32().Should().Be(400);
        exchange.GetProperty("requestBody").GetProperty("contentSha256").GetString()
            .Should().Be("0000000000000000000000000000000000000000000000000000000000000000");
    }

    [Fact]
    public void Parse_UnknownEnvelopeProperty_IsPreservedWhenEvidenceIsAttached()
    {
        var withTicket = MinimalBundle.Replace(
            "\"schemaVersion\": \"1.0\",",
            "\"schemaVersion\": \"1.0\", \"ticket\": \"T-1\",",
            StringComparison.Ordinal);
        var attached = GpDiagnosticBundleDocument.Parse(withTicket).WithEvidence(CleanDraft(), export: null);

        using var json = JsonDocument.Parse(attached.ToJson());
        json.RootElement.GetProperty("schemaVersion").GetString().Should().Be("1.0");
        json.RootElement.GetProperty("ticket").GetString().Should().Be("T-1");
        json.RootElement.GetProperty("gpEvidence").GetProperty("schema").GetString()
            .Should().Be(GpDiagnosticEvidence.SchemaId);
        json.RootElement.GetProperty("gpEvidence").GetProperty("inputDataIncluded").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void WithEvidence_DefaultExport_RedactsSecretsAndOmitsInputBytes()
    {
        var content = Encoding.UTF8.GetBytes("parcels-ok Password=" + Secret);
        var document = GpDiagnosticBundleDocument.Parse(MinimalBundle).WithEvidence(SecretDraft(content), export: null);
        var json = document.ToJson();

        AssertSecretsAbsent(json);
        json.Should().NotContain("contentBase64");
        json.Should().NotContain("parcels-ok");
        json.Should().NotContain("/var/lib/honua/secret-job");
        json.Should().Contain("[REDACTED");

        using var parsed = JsonDocument.Parse(json);
        var evidence = parsed.RootElement.GetProperty("gpEvidence");
        evidence.GetProperty("runtime").GetProperty("workerImage").GetString()
            .Should().Be("ghcr.io/honua-io/honua-gp-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        evidence.GetProperty("release").GetProperty("sourceRepository").GetString().Should().Be("honua-io/honua-samples");
        evidence.GetProperty("inputDataIncluded").GetBoolean().Should().BeFalse();
        evidence.TryGetProperty("inputExport", out _).Should().BeFalse();
        var input = evidence.GetProperty("inputs").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "parcels");
        input.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(content)));
        input.GetProperty("byteSize").GetInt64().Should().Be(content.Length);
        input.TryGetProperty("contentBase64", out _).Should().BeFalse();
        input.GetProperty("reference").GetString().Should().NotContain(Secret);
        parsed.RootElement.GetProperty("schemaVersion").GetString().Should().Be("1.0");

        var reloaded = GpDiagnosticBundleDocument.Parse(json);
        reloaded.Evidence.Should().NotBeNull();
        reloaded.Evidence!.InputDataIncluded.Should().BeFalse();
        reloaded.Evidence.InputExport.Should().BeNull();
        AssertSecretsAbsent(reloaded.ToJson());
    }

    [Fact]
    public void WithEvidence_InputExportRequestedWithoutAuthorization_Refuses()
    {
        var content = Encoding.UTF8.GetBytes("parcels-ok Password=" + Secret);
        var document = GpDiagnosticBundleDocument.Parse(MinimalBundle);
        var when = new DateTimeOffset(2026, 10, 27, 18, 40, 0, TimeSpan.Zero);
        GpDiagnosticInputExportRequest[] requests =
        [
            new() { IncludeInputData = true },
            new() { IncludeInputData = true, AuthorizedBy = "  ", AuthorizedAt = when, InputIds = ["parcels"] },
            new() { IncludeInputData = true, AuthorizedBy = "ada@honua.io", AuthorizedAt = when, InputIds = [] },
            new() { IncludeInputData = true, AuthorizedBy = "ada@honua.io", AuthorizedAt = when, InputIds = ["  "] },
            new() { IncludeInputData = true, AuthorizedBy = "ada@honua.io", InputIds = ["parcels"] },
            new() { IncludeInputData = true, AuthorizedBy = "Password=" + Secret, AuthorizedAt = when, InputIds = ["parcels"] }
        ];

        foreach (var request in requests)
        {
            var act = () => document.WithEvidence(SecretDraft(content), request);
            var exception = act.Should().Throw<GpDiagnosticExportUnauthorizedException>().Which;
            exception.Message.Should().NotContain(Secret);
            exception.Message.Should().NotContain("parcels-ok");
            exception.Message.ToLowerInvariant().Should().Contain("authoriz");
        }

        document.Evidence.Should().BeNull("a refusal must not attach evidence to the original bundle");
        document.ToJson().Should().NotContain(Secret);
        document.ToJson().Should().NotContain("contentBase64");
    }

    [Fact]
    public void WithEvidence_AuthorizationWithoutExportChoice_OmitsInputBytes()
    {
        var content = Encoding.UTF8.GetBytes("parcels-ok");
        var request = new GpDiagnosticInputExportRequest
        {
            IncludeInputData = false,
            AuthorizedBy = "ada@honua.io",
            AuthorizedAt = new DateTimeOffset(2026, 10, 27, 18, 40, 0, TimeSpan.Zero),
            InputIds = ["parcels"]
        };

        var json = GpDiagnosticBundleDocument.Parse(MinimalBundle).WithEvidence(SecretDraft(content), request).ToJson();

        json.Should().NotContain("contentBase64");
        json.Should().NotContain("parcels-ok");
        json.Should().NotContain("ada@honua.io");
        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("gpEvidence").GetProperty("inputDataIncluded").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void WithEvidence_AuthorizedInputExport_RecordsAuthorizerAndRedactsCredentials()
    {
        var content = Encoding.UTF8.GetBytes("parcels-ok Password=" + Secret);
        var when = new DateTimeOffset(2026, 10, 27, 18, 40, 0, TimeSpan.Zero);
        var request = new GpDiagnosticInputExportRequest
        {
            IncludeInputData = true,
            AuthorizedBy = "ada@honua.io",
            AuthorizedAt = when,
            InputIds = ["parcels"]
        };

        var json = GpDiagnosticBundleDocument.Parse(MinimalBundle).WithEvidence(SecretDraft(content), request).ToJson();

        AssertSecretsAbsent(json);
        using var parsed = JsonDocument.Parse(json);
        var evidence = parsed.RootElement.GetProperty("gpEvidence");
        evidence.GetProperty("inputDataIncluded").GetBoolean().Should().BeTrue();
        var export = evidence.GetProperty("inputExport");
        export.GetProperty("authorizedBy").GetString().Should().Be("ada@honua.io");
        export.GetProperty("authorizedAt").GetDateTimeOffset().Should().Be(when);
        var included = export.GetProperty("included").EnumerateArray().Single();
        included.GetProperty("id").GetString().Should().Be("parcels");
        included.GetProperty("contentIncluded").GetBoolean().Should().BeTrue();
        included.GetProperty("redactionApplied").GetBoolean().Should().BeTrue();
        included.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(content)));
        var embedded = Convert.FromBase64String(included.GetProperty("contentBase64").GetString()!);
        var text = Encoding.UTF8.GetString(embedded);
        text.Should().Contain("parcels-ok");
        text.Should().NotContain(Secret);
        included.GetProperty("contentSha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(embedded)));
        evidence.GetProperty("inputs").EnumerateArray().Select(item => item.TryGetProperty("contentBase64", out _))
            .Should().OnlyContain(present => !present);
    }

    [Fact]
    public void Parse_EvidenceThatIncludesInputsWithoutAuthorization_Refuses()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("parcels-ok Password=" + Secret));
        var json = """
            {
              "schemaVersion": "1.0",
              "contentClassification": "internal",
              "consent": { "redactionAcknowledged": true, "shareWithSupport": false },
              "envelopes": [ { "method": "GET", "normalizedPath": "/api/v1/jobs/{id}" } ],
              "gpEvidence": {
                "schema": "honua.gp-diagnostic-evidence/v1",
                "jobId": "job-9",
                "inputDataIncluded": true,
                "inputs": [ { "id": "parcels", "role": "input" } ],
                "inputExport": {
                  "authorizedBy": " ",
                  "authorizedAt": "2026-10-27T18:40:00Z",
                  "included": [ { "id": "parcels", "contentIncluded": true, "contentBase64": "PAYLOAD" } ]
                }
              }
            }
            """.Replace("PAYLOAD", payload, StringComparison.Ordinal);

        var act = () => GpDiagnosticBundleDocument.Parse(json);
        var exception = act.Should().Throw<GpDiagnosticExportUnauthorizedException>().Which;
        exception.Message.Should().NotContain(Secret);
        exception.Message.Should().NotContain("parcels-ok");
    }

    [Fact]
    public void Parse_EvidenceWithSecretInError_RedactsOnLoad()
    {
        var json = $$"""
            {
              "schemaVersion": "1.0",
              "contentClassification": "internal",
              "consent": { "redactionAcknowledged": true, "shareWithSupport": false },
              "envelopes": [ { "method": "GET", "normalizedPath": "/api/v1/jobs/{id}" } ],
              "gpEvidence": {
                "schema": "honua.gp-diagnostic-evidence/v1",
                "jobId": "job-9",
                "inputDataIncluded": false,
                "error": { "message": "Password={{Hunter}} Bearer {{AwsKey}}" },
                "inputs": []
              }
            }
            """;

        var document = GpDiagnosticBundleDocument.Parse(json);

        document.Evidence!.Error!.Message.Should().NotContain(Hunter);
        document.Evidence.Error.Message.Should().NotContain(AwsKey);
        document.ToJson().Should().NotContain(Hunter);
        document.ToJson().Should().NotContain(AwsKey);
    }

    private static GpDiagnosticEvidenceDraft CleanDraft()
        => new()
        {
            JobId = "job-1",
            ReleaseState = "complete",
            ServerVersion = "2026.2.0",
            Runtime = "python"
        };

    private static GpDiagnosticEvidenceDraft SecretDraft(byte[] content)
        => new()
        {
            JobId = "job-1",
            ReleaseState = "partial",
            SourceRepository = "honua-io/honua-samples",
            SourceRef = "0123456789abcdef0123456789abcdef01234567",
            ServerVersion = "2026.2.0",
            Runtime = "python",
            WorkerImage = "ghcr.io/honua-io/honua-gp-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            LockDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ErrorMessage = "gdalwarp failed Password=" + Secret + " Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature /var/lib/honua/secret-job",
            ErrorClassification = "execution_failed",
            StderrExcerpt = "key " + AwsKey,
            Events =
            [
                new GpDiagnosticEventDraft
                {
                    Timestamp = new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero),
                    Level = "error",
                    Message = "postgresql://analyst:" + PostgresSecret + "@db.internal:5432/prod",
                    Attributes = [new GpDiagnosticAttributeDraft { Name = "connectionString", Value = "Host=db.internal;Password=" + Secret }]
                }
            ],
            Inputs =
            [
                new GpDiagnosticInputDraft
                {
                    Id = "parcels",
                    Role = "dataset",
                    MediaType = "application/geo+json",
                    Reference = "Host=db.internal;Username=app;Password=" + Secret,
                    Content = content
                },
                new GpDiagnosticInputDraft { Id = "layer", Role = "resource-ref", Reference = "layer:parcels" }
            ]
        };

    private static void AssertSecretsAbsent(string json)
    {
        json.Should().NotContain(Secret);
        json.Should().NotContain(PostgresSecret);
        json.Should().NotContain(AwsKey);
        json.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
        json.Should().NotContain("analyst:");
    }
}
