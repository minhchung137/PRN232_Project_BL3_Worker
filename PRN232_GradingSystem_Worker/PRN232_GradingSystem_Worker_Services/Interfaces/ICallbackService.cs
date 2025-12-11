using System.Threading;
using System.Threading.Tasks;
using PRN232_GradingSystem_Worker_Services.Models;

namespace PRN232_GradingSystem_Worker_Services.Interfaces;

/// <summary>
/// Service for sending callback results to Main Service
/// </summary>
public interface ICallbackService
{
    /// <summary>
    /// Sends grading result to Main Service callback API
    /// </summary>
    /// <param name="submissionId">The ID of the submission</param>
    /// <param name="result">The grading result</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendResultAsync(string submissionId, GradingResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Sends grade detail request to Main Service callback API
    /// </summary>
    /// <param name="request">The grade detail request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendGradeDetailAsync(GradeDetailRequest request, CancellationToken cancellationToken);
}

