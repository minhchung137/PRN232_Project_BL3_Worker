using System;
using System.Threading;
using System.Threading.Tasks;
using PRN232_GradingSystem_Worker_Repo.Models;

namespace PRN232_GradingSystem_Worker_Services.Interfaces;

/// <summary>
/// Service for detecting duplicate code between submissions
/// </summary>
public interface IDuplicateDetectionService
{
    /// <summary>
    /// Analyzes a submission and detects duplicates against other submissions in the same exam/examiner
    /// </summary>
    /// <param name="submission">Submission to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of duplicate detection results</returns>
    Task<DuplicateDetectionResult> DetectDuplicatesAsync(Submission submission, CancellationToken cancellationToken);

    /// <summary>
    /// Extracts code units (functions, classes, etc.) from a submission
    /// </summary>
    Task ExtractCodeUnitsAsync(Submission submission, string projectPath, CancellationToken cancellationToken);

    /// <summary>
    /// Calculates fingerprints for a submission using k-gram + Winnowing algorithm
    /// </summary>
    Task CalculateFingerprintsAsync(Submission submission, CancellationToken cancellationToken);

    /// <summary>
    /// Calculates SimHash/Minhash signatures for a submission
    /// </summary>
    Task CalculateSignaturesAsync(Submission submission, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures Exam, Examiner exist and creates Submission with CodeFiles from project path
    /// </summary>
    Task PrepareSubmissionAsync(Submission submission, string projectPath, CancellationToken cancellationToken);
}

/// <summary>
/// Result of duplicate detection between two submissions
/// </summary>
public sealed class DuplicateDetectionResult
{
    public Guid SubmissionId1 { get; set; }
    public Guid SubmissionId2 { get; set; }
    public decimal FingerprintScore { get; set; }
    public decimal VectorScore { get; set; }
    public decimal OverallScore { get; set; }
    public bool IsDuplicate { get; set; }
    public int MatchedUnitsCount { get; set; }
    public string MatchedUnitsJson { get; set; } = "[]";
}

