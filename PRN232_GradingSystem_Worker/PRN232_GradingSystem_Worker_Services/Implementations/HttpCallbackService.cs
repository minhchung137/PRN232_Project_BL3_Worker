using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PRN232_GradingSystem_Worker_Services.Interfaces;
using PRN232_GradingSystem_Worker_Services.Models;

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    /// <summary>
    /// HTTP-based callback service for sending results to Main Service
    /// </summary>
    public sealed class HttpCallbackService : ICallbackService
    {
        private readonly string _callbackUrl;
        private readonly int _timeoutSeconds;
        private readonly IHttpClientFactory _httpClientFactory;

        public HttpCallbackService(string baseUrl, string endpoint, int timeoutSeconds, IHttpClientFactory httpClientFactory)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Base URL cannot be empty", nameof(baseUrl));
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("Endpoint cannot be empty", nameof(endpoint));

            _callbackUrl = $"{baseUrl.TrimEnd('/')}{endpoint}";
            _timeoutSeconds = timeoutSeconds;
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task SendResultAsync(string submissionId, GradingResult result, CancellationToken cancellationToken)
        {
            var requestBody = new
            {
                SubmissionId = submissionId,
                Success = result.Success,
                AutoScore = result.AutoScore,
                TotalTests = result.TotalTests,
                PassedTests = result.PassedTests,
                Note = result.Note,
                DurationMs = (int)result.Duration.TotalMilliseconds
            };

            var jsonBody = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);

            var response = await httpClient.PostAsync(_callbackUrl, content, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Callback API returned error. StatusCode: {response.StatusCode}, Response: {errorBody}");
            }
        }

        public async Task SendGradeDetailAsync(GradeDetailRequest request, CancellationToken cancellationToken)
        {
            // Serialize with JsonPropertyName attributes (lowercase property names)
            var jsonBody = JsonSerializer.Serialize(request, new JsonSerializerOptions 
            { 
                WriteIndented = false
            });
            
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);

            // Log the request for debugging
            Console.WriteLine($"[HttpCallbackService] Sending POST to: {_callbackUrl}");
            Console.WriteLine($"[HttpCallbackService] Request body: {jsonBody}");

            var response = await httpClient.PostAsync(_callbackUrl, content, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[HttpCallbackService] Error response: {errorBody}");
                throw new HttpRequestException(
                    $"Callback API returned error. StatusCode: {response.StatusCode}, Response: {errorBody}");
            }
        }
    }
}

