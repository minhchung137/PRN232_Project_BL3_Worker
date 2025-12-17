using PRN232_GradingSystem_Worker_Services.Models;

namespace PRN232_GradingSystem_Worker_Services.Interfaces;

public interface IApiGradingService
{
    Task<ApiGradingResultResponse> GradeApiAsync(string projectPath, string entityName, CancellationToken cancellationToken = default);
}

