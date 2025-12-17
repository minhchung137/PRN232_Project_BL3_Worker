namespace PRN232_GradingSystem_Worker_Services.Models;

public class ApiGradingResultResponse
{
    public string EntityName { get; set; } = string.Empty;
    public double TotalScore { get; set; }
    public double MaxScore { get; set; }
    public List<EndpointScoreResponse> EndpointScores { get; set; } = new();
    public string BuildStatus { get; set; } = string.Empty;
    public string? BuildError { get; set; }
}

public class EndpointScoreResponse
{
    public string Function { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public bool EndpointExists { get; set; }
    public List<CriteriaCheckResponse> CriteriaChecks { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class CriteriaCheckResponse
{
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? Details { get; set; }
}

