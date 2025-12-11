namespace PRN232_GradingSystem_Worker.Configuration;

/// <summary>
/// Configuration model for Main Service callback API
/// </summary>
public sealed class CallbackApiConfiguration
{
    public const string SectionName = "CallbackApi";

    public string BaseUrl { get; set; } = string.Empty;
    public string UpdateResultEndpoint { get; set; } = "/api/grading/update-result";
    public int TimeoutSeconds { get; set; } = 30;
}

