using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PRN232_GradingSystem_Worker_Services.Interfaces;
using PRN232_GradingSystem_Worker_Services.Models.Rubric;

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    public class HttpRubricApiClient : IRubricApiClient
    {
        private readonly string _baseUrl;
        private readonly string _endpointTemplate;
        private readonly int _timeoutSeconds;
        private readonly IHttpClientFactory _httpClientFactory;

        public HttpRubricApiClient(
            string baseUrl,
            string endpointTemplate,
            int timeoutSeconds,
            IHttpClientFactory httpClientFactory)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _endpointTemplate = endpointTemplate;
            _timeoutSeconds = timeoutSeconds;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<ExamRubricDto> GetRubricAsync(string examCode, CancellationToken ct)
        {
            var endpoint = _endpointTemplate.Replace("{examCode}", Uri.EscapeDataString(examCode));

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);

            var url = $"{_baseUrl}{endpoint}";

            var res = await http.GetFromJsonAsync<ApiResponse<ExamRubricDto>>(
                url,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);

            if (res == null)
                throw new Exception("Rubric API returned empty response.");

            if (!res.Success || res.Data == null)
                throw new Exception($"Rubric API failed: {res.Message}");

            return res.Data;
        }
    }
}