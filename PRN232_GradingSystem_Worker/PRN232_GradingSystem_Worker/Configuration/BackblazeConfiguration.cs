namespace PRN232_GradingSystem_Worker.Configuration;

/// <summary>
/// Configuration model for Backblaze B2 settings
/// </summary>
public sealed class BackblazeConfiguration
{
    public const string SectionName = "Backblaze";

    public string Mode { get; set; } = "Native";
    public string KeyId { get; set; } = string.Empty;
    public string ApplicationKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public string ApiEndpoint { get; set; } = "https://api.backblazeb2.com";
    public string S3Endpoint { get; set; } = string.Empty;
}

