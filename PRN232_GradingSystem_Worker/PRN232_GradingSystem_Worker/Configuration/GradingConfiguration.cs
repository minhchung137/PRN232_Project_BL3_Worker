namespace PRN232_GradingSystem_Worker.Configuration;

/// <summary>
/// Configuration model for Grading Worker settings
/// </summary>
public sealed class GradingConfiguration
{
    public const string SectionName = "Grading";

    public string WorkingDirectory { get; set; } = string.Empty;
    public string ResetScriptPath { get; set; } = string.Empty;
}

