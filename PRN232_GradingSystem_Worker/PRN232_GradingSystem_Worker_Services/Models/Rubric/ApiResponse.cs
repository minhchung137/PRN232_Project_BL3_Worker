using System.Text.Json.Serialization;

namespace PRN232_GradingSystem_Worker_Services.Models.Rubric
{
    public class ApiResponse<T>
    {
        [JsonPropertyName("success")] public bool Success { get; set; }

        [JsonPropertyName("message")] public string? Message { get; set; }

        [JsonPropertyName("statusCode")] public int StatusCode { get; set; }

        [JsonPropertyName("data")] public T? Data { get; set; }
    }
}