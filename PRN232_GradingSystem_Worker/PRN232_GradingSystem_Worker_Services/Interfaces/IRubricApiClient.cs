using PRN232_GradingSystem_Worker_Services.Models.Rubric;

namespace PRN232_GradingSystem_Worker_Services.Interfaces
{
    public interface IRubricApiClient
    {
        Task<ExamRubricDto> GetRubricAsync(string examCode, CancellationToken ct);
    }
}