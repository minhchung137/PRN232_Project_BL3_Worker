using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PRN232_GradingSystem_Worker_Repo.Models;
using PRN232_GradingSystem_Worker_Services.Models;

namespace PRN232_GradingSystem_Worker_Services.Interfaces
{
    public interface IGradingPipeline
    {
        Task<GradingResult> ProcessSubmissionAsync(
            string submissionId,
            string fileUrl,
            string? examCode,
            string? studentId,
            string? examinerCode,
            string? entityName,
            CancellationToken cancellationToken);
    }

    public sealed class GradingResult
    {
        public bool Success { get; set; }
        public int AutoScore { get; set; }
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public string? Note { get; set; }
        public TimeSpan Duration { get; set; }
        public List<GradingStepLog> StepLogs { get; set; } = new();
        public TestResultDetail? TestResultDetail { get; set; }
        public ApiGradingResultResponse? ApiGradingResult { get; set; } // Kết quả API grading để truyền trực tiếp sang API
    }

    public sealed class GradingStepLog
    {
        public string Step { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = "Info"; // Info, Warning, Error
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
