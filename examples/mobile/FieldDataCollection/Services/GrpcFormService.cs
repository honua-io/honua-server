// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Grpc.Net.Client;
using Honua.Server.Features.Grpc.Proto;
using Microsoft.Extensions.Logging;

namespace FieldDataCollection.Services;

/// <summary>
/// gRPC-native form service client implementation.
/// Provides efficient, type-safe form operations optimized for mobile devices.
/// </summary>
public class GrpcFormService : IGrpcFormService, IDisposable
{
    private readonly FormService.FormServiceClient _client;
    private readonly GrpcChannel _channel;
    private readonly ILogger<GrpcFormService> _logger;
    private readonly SemaphoreSlim _streamSemaphore = new(1, 1);

    public GrpcFormService(
        string serverAddress,
        ILogger<GrpcFormService> logger)
    {
        _logger = logger;

        // Configure gRPC channel with mobile optimizations
        var channelOptions = new GrpcChannelOptions
        {
            // Mobile-optimized settings
            HttpHandler = CreateHttpHandler(),
            MaxReceiveMessageSize = 4 * 1024 * 1024, // 4MB max message size
            MaxSendMessageSize = 4 * 1024 * 1024,
            Credentials = ChannelCredentials.SecureSsl,

            // Keep-alive for mobile networks
            KeepAliveTime = TimeSpan.FromMinutes(1),
            KeepAliveTimeout = TimeSpan.FromSeconds(5),
            KeepAliveWithoutCalls = true,

            // Compression for bandwidth efficiency
            CompressionProviders = new List<ICompressionProvider>
            {
                new GzipCompressionProvider(System.IO.Compression.CompressionLevel.Optimal)
            }
        };

        _channel = GrpcChannel.ForAddress(serverAddress, channelOptions);
        _client = new FormService.FormServiceClient(_channel);

        _logger.LogInformation("Initialized gRPC form service client for {ServerAddress}", serverAddress);
    }

    public async Task<GetFormDefinitionResponse> GetFormDefinitionAsync(
        string formId,
        string serviceId,
        int layerId,
        MobileCapabilities? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GetFormDefinitionRequest
            {
                FormId = formId,
                ServiceId = serviceId,
                LayerId = layerId,
                MobileCapabilities = capabilities ?? GrpcFormExtensions.GetCurrentDeviceCapabilities()
            };

            var headers = await GetAuthHeadersAsync();
            var response = await _client.GetFormDefinitionAsync(request, headers, cancellationToken: cancellationToken);

            _logger.LogInformation("Retrieved form definition for {FormId} with {ControlCount} controls",
                formId, response.Form.Controls.Count);

            return response;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            _logger.LogWarning("Form {FormId} not found", formId);
            throw new FormNotFoundException($"Form '{formId}' was not found", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get form definition for {FormId}", formId);
            throw;
        }
    }

    public async Task<SubmitFormDataResponse> SubmitFormDataAsync(
        string formId,
        FormInstance instance,
        List<FormAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = new SubmissionMetadata
            {
                DeviceId = DeviceInfo.Name,
                AppVersion = AppInfo.VersionString,
                Platform = DeviceInfo.Platform.ToString(),
                SubmissionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            // Add current location if available
            try
            {
                var location = await Geolocation.GetLocationAsync(new GeolocationRequest
                {
                    DesiredAccuracy = GeolocationAccuracy.Medium,
                    Timeout = TimeSpan.FromSeconds(5)
                });

                if (location != null)
                {
                    metadata.Latitude = location.Latitude;
                    metadata.Longitude = location.Longitude;
                }
            }
            catch
            {
                // Location not available, continue without it
            }

            var request = new SubmitFormDataRequest
            {
                FormId = formId,
                FormVersion = "latest", // Could be tracked per form
                Instance = instance,
                Metadata = metadata
            };

            request.Attachments.AddRange(attachments);

            var headers = await GetAuthHeadersAsync();
            var response = await _client.SubmitFormDataAsync(request, headers, cancellationToken: cancellationToken);

            _logger.LogInformation("Submitted form {FormId}, instance {InstanceId}, created feature {FeatureId}",
                formId, instance.InstanceId, response.CreatedFeatureId);

            return response;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            _logger.LogWarning("Form submission validation failed for {FormId}: {Message}", formId, ex.Status.Detail);
            throw new FormValidationException("Form data validation failed", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit form {FormId}", formId);
            throw;
        }
    }

    public async Task<ValidateFormDataResponse> ValidateFormDataAsync(
        string formId,
        FormInstance instance,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new ValidateFormDataRequest
            {
                FormId = formId,
                Instance = instance
            };

            var headers = await GetAuthHeadersAsync();
            var response = await _client.ValidateFormDataAsync(request, headers, cancellationToken: cancellationToken);

            _logger.LogDebug("Validated form {FormId}, {IssueCount} issues found",
                formId, response.Issues.Count);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate form {FormId}", formId);
            throw;
        }
    }

    public async IAsyncEnumerable<FormUpdateResponse> StreamFormUpdatesAsync(
        string sessionId,
        string formId,
        string instanceId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _streamSemaphore.WaitAsync(cancellationToken);

        try
        {
            var headers = await GetAuthHeadersAsync();
            using var call = _client.StreamFormUpdates(headers, cancellationToken: cancellationToken);

            // Send initial join message
            await call.RequestStream.WriteAsync(new FormUpdateRequest
            {
                SessionId = sessionId,
                FormId = formId,
                InstanceId = instanceId,
                Update = new FormUpdate
                {
                    UpdateType = UpdateType.UserJoined,
                    UserId = GetCurrentUserId(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            });

            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                _logger.LogDebug("Received form update: {UpdateType} from {UserId}",
                    response.Update.UpdateType, response.Update.UserId);

                yield return response;
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Form update stream cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Form update stream error for session {SessionId}", sessionId);
            throw;
        }
        finally
        {
            _streamSemaphore.Release();
        }
    }

    public async Task SendFormUpdateAsync(
        string sessionId,
        FormUpdate update,
        CancellationToken cancellationToken = default)
    {
        // This would be implemented as part of the bidirectional streaming in StreamFormUpdatesAsync
        // For now, we'll use a simple approach
        _logger.LogDebug("Form update sent: {UpdateType} for field {FieldId}",
            update.UpdateType, update.FieldId);

        await Task.CompletedTask; // Placeholder
    }

    public async Task<GetFormMetadataResponse> GetFormCatalogAsync(
        string? serviceId = null,
        List<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GetFormMetadataRequest
            {
                ServiceId = serviceId ?? ""
            };

            if (tags != null)
            {
                request.Tags.AddRange(tags);
            }

            var headers = await GetAuthHeadersAsync();
            var response = await _client.GetFormMetadataAsync(request, headers, cancellationToken: cancellationToken);

            _logger.LogInformation("Retrieved {FormCount} forms from catalog", response.Forms.Count);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get form catalog");
            throw;
        }
    }

    private static HttpMessageHandler CreateHttpHandler()
    {
        var handler = new HttpClientHandler();

        // Mobile-specific optimizations
        if (handler.SupportsAutomaticDecompression)
        {
            handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                           System.Net.DecompressionMethods.Deflate;
        }

        // Certificate validation for production
        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
        {
            // In production, implement proper certificate validation
            return true;
        };

        return handler;
    }

    private async Task<Metadata> GetAuthHeadersAsync()
    {
        var headers = new Metadata();

        // Get API key from secure storage
        var apiKey = await SecureStorage.GetAsync("honua_api_key");
        if (!string.IsNullOrEmpty(apiKey))
        {
            headers.Add("X-API-Key", apiKey);
        }

        // Add correlation ID for request tracing
        headers.Add("X-Correlation-ID", Guid.NewGuid().ToString());

        // Add client information
        headers.Add("User-Agent", $"HonuaMobile/{AppInfo.VersionString} ({DeviceInfo.Platform})");

        return headers;
    }

    private string GetCurrentUserId()
    {
        // In production, get from authentication service
        return $"user_{DeviceInfo.Name}";
    }

    public void Dispose()
    {
        _streamSemaphore?.Dispose();
        _channel?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Exception thrown when a requested form is not found.
/// </summary>
public class FormNotFoundException : Exception
{
    public FormNotFoundException(string message) : base(message) { }
    public FormNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when form data validation fails.
/// </summary>
public class FormValidationException : Exception
{
    public FormValidationException(string message) : base(message) { }
    public FormValidationException(string message, Exception innerException) : base(message, innerException) { }
}