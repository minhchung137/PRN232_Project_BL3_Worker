using System.Collections.Generic;

namespace PRN232_GradingSystem_Worker_Services.Settings;

public sealed class ValidationSettings
{
    public const string SectionName = "Validation";

    public List<string> ForbiddenKeywords { get; set; } = new() { "Lion" };
}

