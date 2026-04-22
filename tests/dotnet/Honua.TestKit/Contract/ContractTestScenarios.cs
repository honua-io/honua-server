// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Honua.TestKit.Contract;

/// <summary>
/// Contract testing scenarios to ensure API compliance with specifications.
/// </summary>
public static class ContractTestScenarios
{
    /// <summary>
    /// Validates that OGC API Features responses conform to the OGC specification schema.
    /// </summary>
    public static async Task<ContractTestResult> ValidateOgcApiSchema(
        HttpClient client,
        string endpoint,
        string expectedSchemaPath)
    {
        var results = new List<ContractValidation>();

        try
        {
            // Load expected schema
            var schema = await LoadJsonSchema(expectedSchemaPath);

            // Get actual response
            var response = await client.GetAsync(endpoint);
            var content = await response.Content.ReadAsStringAsync();

            // Validate against schema
            var jsonDocument = JsonDocument.Parse(content);
            var token = JToken.Parse(content);
            var isValid = token.IsValid(schema);

            results.Add(new ContractValidation
            {
                TestName = $"OGC Schema Validation: {endpoint}",
                IsValid = isValid,
                Errors = isValid ? new List<string>() : new List<string> { "Schema validation failed" },
                ActualContent = content
            });
        }
        catch (Exception ex)
        {
            results.Add(new ContractValidation
            {
                TestName = $"OGC Schema Validation: {endpoint}",
                IsValid = false,
                Errors = new List<string> { ex.Message },
                ActualContent = ""
            });
        }

        return new ContractTestResult
        {
            TestType = "OGC API Features Schema Validation",
            Validations = results
        };
    }

    /// <summary>
    /// Validates GeoJSON responses conform to RFC 7946 specification.
    /// </summary>
    public static async Task<ContractTestResult> ValidateGeoJsonSchema(
        HttpClient client,
        string endpoint)
    {
        var results = new List<ContractValidation>();

        try
        {
            var response = await client.GetAsync(endpoint);
            var content = await response.Content.ReadAsStringAsync();

            var geoJsonValidation = ValidateGeoJsonStructure(content);
            results.Add(geoJsonValidation);
        }
        catch (Exception ex)
        {
            results.Add(new ContractValidation
            {
                TestName = "GeoJSON Schema Validation",
                IsValid = false,
                Errors = new List<string> { ex.Message },
                ActualContent = ""
            });
        }

        return new ContractTestResult
        {
            TestType = "GeoJSON RFC 7946 Validation",
            Validations = results
        };
    }

    /// <summary>
    /// Tests backwards compatibility by comparing current responses with baseline.
    /// </summary>
    public static async Task<ContractTestResult> ValidateBackwardsCompatibility(
        HttpClient client,
        Dictionary<string, string> endpointBaselines)
    {
        var results = new List<ContractValidation>();

        foreach (var (endpoint, baseline) in endpointBaselines)
        {
            try
            {
                var response = await client.GetAsync(endpoint);
                var currentContent = await response.Content.ReadAsStringAsync();

                var compatibility = ValidateCompatibility(baseline, currentContent);
                compatibility.TestName = $"Backwards Compatibility: {endpoint}";
                results.Add(compatibility);
            }
            catch (Exception ex)
            {
                results.Add(new ContractValidation
                {
                    TestName = $"Backwards Compatibility: {endpoint}",
                    IsValid = false,
                    Errors = new List<string> { ex.Message },
                    ActualContent = ""
                });
            }
        }

        return new ContractTestResult
        {
            TestType = "Backwards Compatibility",
            Validations = results
        };
    }

    /// <summary>
    /// Tests API contract using consumer-driven contract patterns.
    /// </summary>
    public static async Task<ContractTestResult> ValidateConsumerContracts(
        List<ConsumerContract> contracts)
    {
        var results = new List<ContractValidation>();

        foreach (var contract in contracts)
        {
            var mockServer = WireMockServer.Start();

            try
            {
                // Setup mock based on contract expectations
                mockServer
                    .Given(Request.Create()
                        .UsingMethod(contract.ExpectedMethod)
                        .WithPath(contract.ExpectedPath))
                    .RespondWith(Response.Create()
                        .WithStatusCode(contract.ExpectedStatusCode)
                        .WithHeader("Content-Type", contract.ExpectedContentType)
                        .WithBody(contract.ExpectedResponse));

                // Test consumer expectations
                using var client = new HttpClient { BaseAddress = new Uri(mockServer.Urls[0]) };
                var response = await client.SendAsync(contract.CreateRequest());

                var validation = new ContractValidation
                {
                    TestName = $"Consumer Contract: {contract.ConsumerName}",
                    IsValid = response.StatusCode == (System.Net.HttpStatusCode)contract.ExpectedStatusCode,
                    Errors = new List<string>(),
                    ActualContent = await response.Content.ReadAsStringAsync()
                };

                if (!validation.IsValid)
                {
                    validation.Errors.Add($"Expected status {contract.ExpectedStatusCode}, got {(int)response.StatusCode}");
                }

                results.Add(validation);
            }
            catch (Exception ex)
            {
                results.Add(new ContractValidation
                {
                    TestName = $"Consumer Contract: {contract.ConsumerName}",
                    IsValid = false,
                    Errors = new List<string> { ex.Message },
                    ActualContent = ""
                });
            }
            finally
            {
                mockServer.Stop();
            }
        }

        return new ContractTestResult
        {
            TestType = "Consumer-Driven Contracts",
            Validations = results
        };
    }

    /// <summary>
    /// Validates HTTP semantics compliance (status codes, headers, etc.).
    /// </summary>
    public static async Task<ContractTestResult> ValidateHttpSemantics(
        HttpClient client,
        Dictionary<string, HttpSemanticsExpectation> expectations)
    {
        var results = new List<ContractValidation>();

        foreach (var (endpoint, expectation) in expectations)
        {
            try
            {
                var response = await client.SendAsync(expectation.CreateRequest(endpoint));

                var errors = new List<string>();

                // Validate status code
                if (expectation.ExpectedStatusCodes?.Contains(response.StatusCode) == false)
                {
                    errors.Add($"Unexpected status code: {response.StatusCode}");
                }

                // Validate required headers
                if (expectation.RequiredHeaders != null)
                {
                    foreach (var requiredHeader in expectation.RequiredHeaders)
                    {
                        if (!HasHeader(response, requiredHeader))
                        {
                            errors.Add($"Missing required header: {requiredHeader}");
                        }
                    }
                }

                // Validate content type
                if (expectation.ExpectedContentType != null)
                {
                    var actualContentType = response.Content.Headers.ContentType?.MediaType;
                    if (actualContentType != expectation.ExpectedContentType)
                    {
                        errors.Add($"Expected content type {expectation.ExpectedContentType}, got {actualContentType}");
                    }
                }

                results.Add(new ContractValidation
                {
                    TestName = $"HTTP Semantics: {endpoint}",
                    IsValid = errors.Count == 0,
                    Errors = errors,
                    ActualContent = await response.Content.ReadAsStringAsync()
                });
            }
            catch (Exception ex)
            {
                results.Add(new ContractValidation
                {
                    TestName = $"HTTP Semantics: {endpoint}",
                    IsValid = false,
                    Errors = new List<string> { ex.Message },
                    ActualContent = ""
                });
            }
        }

        return new ContractTestResult
        {
            TestType = "HTTP Semantics Validation",
            Validations = results
        };
    }

    private static async Task<JSchema> LoadJsonSchema(string schemaPath)
    {
        if (File.Exists(schemaPath))
        {
            var schemaJson = await File.ReadAllTextAsync(schemaPath);
            return JSchema.Parse(schemaJson);
        }

        // Fallback to embedded or default schemas
        return CreateDefaultOgcSchema();
    }

    private static JSchema CreateDefaultOgcSchema()
    {
        var schemaJson = """
        {
          "type": "object",
          "required": ["type", "features"],
          "properties": {
            "type": { "type": "string", "enum": ["FeatureCollection"] },
            "features": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["type", "properties"],
                "properties": {
                  "type": { "type": "string", "enum": ["Feature"] },
                  "properties": { "type": "object" },
                  "geometry": {
                    "oneOf": [
                      { "type": "null" },
                      {
                        "type": "object",
                        "required": ["type", "coordinates"],
                        "properties": {
                          "type": { "type": "string" },
                          "coordinates": { "type": "array" }
                        }
                      }
                    ]
                  }
                }
              }
            }
          }
        }
        """;

        return JSchema.Parse(schemaJson);
    }

    private static ContractValidation ValidateGeoJsonStructure(string geoJsonContent)
    {
        var errors = new List<string>();

        try
        {
            var jsonDoc = JsonDocument.Parse(geoJsonContent);
            var root = jsonDoc.RootElement;

            // Validate basic GeoJSON structure
            if (!root.TryGetProperty("type", out var typeProperty))
            {
                errors.Add("Missing 'type' property");
            }
            else
            {
                var type = typeProperty.GetString();
                if (type != "FeatureCollection" && type != "Feature")
                {
                    errors.Add($"Invalid type: {type}");
                }

                if (type == "FeatureCollection")
                {
                    if (!root.TryGetProperty("features", out var features) || !features.ValueKind.Equals(JsonValueKind.Array))
                    {
                        errors.Add("FeatureCollection must have 'features' array");
                    }
                }
            }

            // Validate coordinate order (longitude, latitude)
            ValidateCoordinateOrder(root, errors);
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
        }

        return new ContractValidation
        {
            TestName = "GeoJSON Structure Validation",
            IsValid = errors.Count == 0,
            Errors = errors,
            ActualContent = geoJsonContent
        };
    }

    private static void ValidateCoordinateOrder(JsonElement element, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("coordinates", out var coords) && coords.ValueKind == JsonValueKind.Array)
            {
                foreach (var coord in coords.EnumerateArray())
                {
                    if (coord.ValueKind == JsonValueKind.Array && coord.GetArrayLength() >= 2)
                    {
                        var lon = coord[0].GetDouble();
                        var lat = coord[1].GetDouble();

                        if (Math.Abs(lon) > 180 || Math.Abs(lat) > 90)
                        {
                            errors.Add($"Invalid coordinates: [{lon}, {lat}] - longitude must be [-180, 180], latitude [-90, 90]");
                        }
                    }
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateCoordinateOrder(item, errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateCoordinateOrder(property.Value, errors);
            }
        }
    }

    private static ContractValidation ValidateCompatibility(string baseline, string current)
    {
        var errors = new List<string>();

        try
        {
            var baselineDoc = JsonDocument.Parse(baseline);
            var currentDoc = JsonDocument.Parse(current);

            // Check that all baseline properties still exist in current
            ValidatePropertyCompatibility(baselineDoc.RootElement, currentDoc.RootElement, "", errors);
        }
        catch (Exception ex)
        {
            errors.Add($"Compatibility validation failed: {ex.Message}");
        }

        return new ContractValidation
        {
            TestName = "Backwards Compatibility",
            IsValid = errors.Count == 0,
            Errors = errors,
            ActualContent = current
        };
    }

    private static void ValidatePropertyCompatibility(JsonElement baseline, JsonElement current, string path, List<string> errors)
    {
        if (baseline.ValueKind != current.ValueKind)
        {
            errors.Add($"Type changed at {path}: {baseline.ValueKind} -> {current.ValueKind}");
            return;
        }

        if (baseline.ValueKind == JsonValueKind.Object)
        {
            foreach (var baselineProp in baseline.EnumerateObject())
            {
                var propPath = string.IsNullOrEmpty(path) ? baselineProp.Name : $"{path}.{baselineProp.Name}";

                if (!current.TryGetProperty(baselineProp.Name, out var currentProp))
                {
                    errors.Add($"Property removed: {propPath}");
                }
                else
                {
                    ValidatePropertyCompatibility(baselineProp.Value, currentProp, propPath, errors);
                }
            }
        }
    }

    private static bool HasHeader(HttpResponseMessage response, string headerName)
    {
        if (IsContentHeader(headerName))
        {
            return response.Content?.Headers.Contains(headerName) == true;
        }

        if (response.Headers.Contains(headerName))
        {
            return true;
        }

        return response.Content?.Headers.Contains(headerName) == true;
    }

    private static bool IsContentHeader(string headerName)
    {
        return _contentHeaders.Contains(headerName);
    }

    private static readonly HashSet<string> _contentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Length",
        "Content-Disposition",
        "Content-Encoding",
        "Content-Language",
        "Content-Location",
        "Content-Range",
        "Last-Modified",
        "Expires"
    };
}

public class ContractTestResult
{
    public string TestType { get; init; } = "";
    public List<ContractValidation> Validations { get; init; } = new();

    public bool AllValid => Validations.All(v => v.IsValid);
    public int FailedValidations => Validations.Count(v => !v.IsValid);
    public double SuccessRate => Validations.Count > 0 ? (double)Validations.Count(v => v.IsValid) / Validations.Count : 1.0;
}

public class ContractValidation
{
    public string TestName { get; set; } = "";
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = new();
    public string ActualContent { get; init; } = "";
}

public class ConsumerContract
{
    public string ConsumerName { get; init; } = "";
    public string ExpectedMethod { get; init; } = "GET";
    public string ExpectedPath { get; init; } = "";
    public int ExpectedStatusCode { get; init; } = 200;
    public string ExpectedContentType { get; init; } = "application/json";
    public string ExpectedResponse { get; init; } = "";

    public HttpRequestMessage CreateRequest()
    {
        return new HttpRequestMessage(new HttpMethod(ExpectedMethod), ExpectedPath);
    }
}

public class HttpSemanticsExpectation
{
    public string Method { get; init; } = "GET";
    public List<System.Net.HttpStatusCode>? ExpectedStatusCodes { get; init; }
    public List<string>? RequiredHeaders { get; init; }
    public string? ExpectedContentType { get; init; }
    public Dictionary<string, string>? AdditionalHeaders { get; init; }

    public HttpRequestMessage CreateRequest(string endpoint)
    {
        var request = new HttpRequestMessage(new HttpMethod(Method), endpoint);

        if (AdditionalHeaders != null)
        {
            foreach (var header in AdditionalHeaders)
            {
                request.Headers.Add(header.Key, header.Value);
            }
        }

        return request;
    }
}
