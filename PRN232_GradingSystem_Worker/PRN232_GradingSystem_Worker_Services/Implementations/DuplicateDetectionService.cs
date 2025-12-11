using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PRN232_GradingSystem_Worker_Repo.DBContext;
using PRN232_GradingSystem_Worker_Repo.Models;
using PRN232_GradingSystem_Worker_Services.Interfaces;

namespace PRN232_GradingSystem_Worker_Services.Implementations;

/// <summary>
/// Service for detecting duplicate code between submissions
/// </summary>
public sealed class DuplicateDetectionService : IDuplicateDetectionService
{
    private readonly PRN232_Grading_System_GradingContext _context;
    private readonly FingerprintService _fingerprintService;
    private readonly SignatureService _signatureService;
    private readonly CodeParserService _codeParserService;
    private readonly ILogger<DuplicateDetectionService> _logger;

    private const decimal VectorThreshold = 0.7m;

    // Performance limits
    private const int MaxUnitsPerFile = 150;               // cap units per file
    private const int MaxUnitsPerSubmission = 4000;        // cap units per submission
    private const int MaxFingerprintsPerUnit = 120;        // cap fingerprints per unit
    private const long MaxFileSizeBytes = 200_000;         // skip files larger than ~200 KB

    public DuplicateDetectionService(
        PRN232_Grading_System_GradingContext context,
        FingerprintService fingerprintService,
        SignatureService signatureService,
        CodeParserService codeParserService,
        ILogger<DuplicateDetectionService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _fingerprintService = fingerprintService ?? throw new ArgumentNullException(nameof(fingerprintService));
        _signatureService = signatureService ?? throw new ArgumentNullException(nameof(signatureService));
        _codeParserService = codeParserService ?? throw new ArgumentNullException(nameof(codeParserService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ExtractCodeUnitsAsync(Submission submission, string projectPath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Extracting code units for submission {SubmissionId} from {ProjectPath}", 
            submission.SubmissionId, projectPath);

        // Check if code units already exist
        var existingUnits = await _context.CodeUnits
            .Where(u => u.SubmissionId == submission.SubmissionId)
            .ToListAsync(cancellationToken);
        
        if (existingUnits.Any())
        {
            _logger.LogInformation("Code units already exist for submission {SubmissionId}, skipping", submission.SubmissionId);
            return;
        }

        var codeFiles = await _context.CodeFiles
            .Where(f => f.SubmissionId == submission.SubmissionId)
            .ToListAsync(cancellationToken);

        var totalUnits = 0;
        foreach (var codeFile in codeFiles)
        {
            var filePath = Path.Combine(projectPath, codeFile.RelPath);
            
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                continue;
            }

            var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken);
            var language = _codeParserService.DetectLanguage(codeFile.RelPath);
            var codeUnits = _codeParserService.ExtractCodeUnits(fileContent, codeFile.RelPath, language)
                .Take(MaxUnitsPerFile)
                .ToList();

            foreach (var unitInfo in codeUnits)
            {
                if (totalUnits >= MaxUnitsPerSubmission)
                    break;
                var codeUnit = new CodeUnit
                {
                    UnitId = Guid.NewGuid(),
                    SubmissionId = submission.SubmissionId,
                    FileId = codeFile.FileId,
                    UnitKind = unitInfo.Kind,
                    UnitKey = unitInfo.Key,
                    Content = unitInfo.Content,
                    StartLine = unitInfo.StartLine,
                    EndLine = unitInfo.EndLine,
                    ContentHash = ComputeHash(unitInfo.Content),
                    CreatedAt = DateTime.UtcNow
                };

                _context.CodeUnits.Add(codeUnit);
                totalUnits++;
            }
            if (totalUnits >= MaxUnitsPerSubmission)
                break;
        }

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogDebug("Extracted code units for {Count} files", codeFiles.Count);
    }

    /// <inheritdoc />
    public async Task CalculateFingerprintsAsync(Submission submission, CancellationToken cancellationToken)
    {
        // Vector DB only: no local fingerprint generation
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task CalculateSignaturesAsync(Submission submission, CancellationToken cancellationToken)
    {
        // Vector DB only: no local signature generation
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<DuplicateDetectionResult> DetectDuplicatesAsync(Submission submission, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[DupDetect] Start for submission {SubmissionId} (Exam: {ExamId}, Examiner: {ExaminerId})", 
            submission.SubmissionId, submission.ExamId, submission.ExaminerId);

        // Optional: seed vector matches locally (Jaccard over ContentHash) to approximate vector DB
        await SeedVectorMatchesAsync(submission, cancellationToken);

        // Use MatchResults (vector DB) as the source of truth
        var bestFromSrc = await _context.MatchResults
            .Where(m => m.ExamId == submission.ExamId && m.ExaminerId == submission.ExaminerId && m.SrcSubmissionId == submission.SubmissionId)
            .OrderByDescending(m => m.Score)
            .FirstOrDefaultAsync(cancellationToken);

        var bestFromTgt = await _context.MatchResults
            .Where(m => m.ExamId == submission.ExamId && m.ExaminerId == submission.ExaminerId && m.TgtSubmissionId == submission.SubmissionId)
            .OrderByDescending(m => m.Score)
            .FirstOrDefaultAsync(cancellationToken);

        var best = bestFromSrc;
        if (bestFromTgt != null && (best == null || bestFromTgt.Score > best.Score))
            best = bestFromTgt;

        if (best == null)
        {
            _logger.LogInformation("[DupDetect] No vector matches found");
            return new DuplicateDetectionResult { SubmissionId1 = submission.SubmissionId, IsDuplicate = false };
        }

        var otherId = best.SrcSubmissionId == submission.SubmissionId ? best.TgtSubmissionId : best.SrcSubmissionId;
        var result = new DuplicateDetectionResult
        {
            SubmissionId1 = submission.SubmissionId,
            SubmissionId2 = otherId,
            VectorScore = best.Score,
            OverallScore = best.Score,
            IsDuplicate = best.Score >= VectorThreshold
        };

        if (result.IsDuplicate && result.SubmissionId2 != Guid.Empty)
        {
            // Normalize pair ordering to avoid unique index conflicts (store smaller GUID first)
            var a = submission.SubmissionId;
            var b = result.SubmissionId2;
            var (s1, s2) = a.CompareTo(b) < 0 ? (a, b) : (b, a);

            var existingDup = await _context.DuplicateDetections.FirstOrDefaultAsync(
                d => d.ExamId == submission.ExamId && d.ExaminerId == submission.ExaminerId && d.SubmissionId1 == s1 && d.SubmissionId2 == s2,
                cancellationToken);

            if (existingDup == null)
            {
                var otherSub = await _context.Submissions.FirstOrDefaultAsync(s => s.SubmissionId == b, cancellationToken);
                var duplicateDetection = new DuplicateDetection
                {
                    ExamId = submission.ExamId,
                    ExaminerId = submission.ExaminerId,
                    SubmissionId1 = s1,
                    SubmissionId2 = s2,
                    StudentId1 = s1 == submission.SubmissionId ? submission.StudentId : (otherSub?.StudentId ?? Guid.Empty),
                    StudentId2 = s2 == submission.SubmissionId ? submission.StudentId : (otherSub?.StudentId ?? Guid.Empty),
                    VectorScore = result.VectorScore,
                    IsDuplicate = true,
                    ThresholdUsed = $"Vector={VectorThreshold}",
                    MatchedUnits = JsonSerializer.Serialize(new List<object>()),
                    CreatedAt = DateTime.UtcNow,
                    Notes = "Detected by vector DB"
                };
                _context.DuplicateDetections.Add(duplicateDetection);
            }
            else
            {
                // Update score if higher, ensure flag remains true
                if (result.VectorScore > existingDup.VectorScore)
                    existingDup.VectorScore = result.VectorScore;
                existingDup.IsDuplicate = true;
                existingDup.ThresholdUsed = $"Vector={VectorThreshold}";
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[DupDetect] Duplicate detected (vector): {Submission1} vs {Submission2}, Score: {Score}", s1, s2, result.VectorScore);
        }
        else
        {
            _logger.LogInformation("[DupDetect] No duplicate found. Best vector score={Score}", result.OverallScore);
        }

        return result;
    }

    private async Task SeedVectorMatchesAsync(Submission submission, CancellationToken cancellationToken)
    {
        try
        {
            // Current units (hash set)
            var currentHashes = await _context.CodeUnits
                .Where(u => u.SubmissionId == submission.SubmissionId)
                .Select(u => Convert.ToBase64String(u.ContentHash))
                .ToListAsync(cancellationToken);

            if (currentHashes.Count == 0)
                return;

            var currentSet = new HashSet<string>(currentHashes);

            // Other submissions in same scope
            var others = await _context.Submissions
                .Where(s => s.ExamId == submission.ExamId && s.ExaminerId == submission.ExaminerId && s.SubmissionId != submission.SubmissionId)
                .Select(s => s.SubmissionId)
                .ToListAsync(cancellationToken);

            foreach (var otherId in others)
            {
                var otherHashes = await _context.CodeUnits
                    .Where(u => u.SubmissionId == otherId)
                    .Select(u => Convert.ToBase64String(u.ContentHash))
                    .ToListAsync(cancellationToken);

                if (otherHashes.Count == 0)
                    continue;

                var otherSet = new HashSet<string>(otherHashes);
                var intersect = currentSet.Intersect(otherSet).Count();
                var union = currentSet.Union(otherSet).Count();
                var jaccard = union == 0 ? 0m : (decimal)intersect / union;

                // Upsert MatchResult in both directions for convenience
                await UpsertMatchAsync(submission.ExamId, submission.ExaminerId, submission.SubmissionId, otherId, jaccard, cancellationToken);
                await UpsertMatchAsync(submission.ExamId, submission.ExaminerId, otherId, submission.SubmissionId, jaccard, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DupDetect] SeedVectorMatches skipped due to error");
        }
    }

    private async Task UpsertMatchAsync(Guid examId, Guid? examinerId, Guid srcId, Guid tgtId, decimal score, CancellationToken ct)
    {
        // Skip self-match by submission
        if (srcId == tgtId) return;

        // Pick representative unit for each submission (first unit)
        var srcUnitId = await _context.CodeUnits
            .Where(u => u.SubmissionId == srcId)
            .OrderBy(u => u.UnitId)
            .Select(u => u.UnitId)
            .FirstOrDefaultAsync(ct);

        var tgtUnitId = await _context.CodeUnits
            .Where(u => u.SubmissionId == tgtId)
            .OrderBy(u => u.UnitId)
            .Select(u => u.UnitId)
            .FirstOrDefaultAsync(ct);

        if (srcUnitId == Guid.Empty || tgtUnitId == Guid.Empty) return; // no units available

        // Ensure units are not identical to satisfy chk_match_src_ne_tgt
        if (srcUnitId == tgtUnitId)
        {
            // try next target unit
            tgtUnitId = await _context.CodeUnits
                .Where(u => u.SubmissionId == tgtId)
                .OrderBy(u => u.UnitId)
                .Select(u => u.UnitId)
                .Skip(1)
                .FirstOrDefaultAsync(ct);
            if (tgtUnitId == Guid.Empty || tgtUnitId == srcUnitId) return;
        }

        var existing = await _context.MatchResults
            .FirstOrDefaultAsync(m => m.ExamId == examId && m.ExaminerId == examinerId && m.SrcSubmissionId == srcId && m.TgtSubmissionId == tgtId, ct);

        if (existing == null)
        {
            var m = new MatchResult
            {
                ExamId = examId,
                ExaminerId = examinerId,
                SrcSubmissionId = srcId,
                TgtSubmissionId = tgtId,
                SrcUnitId = srcUnitId,
                TgtUnitId = tgtUnitId,
                Method = "vector",
                Score = score,
                CreatedAt = DateTime.UtcNow
            };
            _context.MatchResults.Add(m);
        }
        else
        {
            existing.Score = Math.Max(existing.Score, score);
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task PrepareSubmissionAsync(Submission submission, string projectPath, CancellationToken cancellationToken)
    {
            _logger.LogDebug("Preparing submission {SubmissionId} for duplicate detection", submission.SubmissionId);

        // Note: Exam/Examiner should already be created in ResolveSubmissionAsync
        // Just ensure they exist in DB
        if (submission.ExamId != Guid.Empty)
        {
            var examExists = await _context.Exams.AnyAsync(e => e.ExamId == submission.ExamId, cancellationToken);
            if (!examExists)
            {
                _logger.LogWarning("Exam {ExamId} not found in database. This should not happen!", submission.ExamId);
            }
        }

        if (submission.ExaminerId.HasValue && submission.ExaminerId.Value != Guid.Empty)
        {
            var examinerExists = await _context.Examiners.AnyAsync(e => e.ExaminerId == submission.ExaminerId.Value, cancellationToken);
            if (!examinerExists)
            {
                _logger.LogWarning("Examiner {ExaminerId} not found in database. This should not happen!", submission.ExaminerId);
            }
        }

        // Upsert Submission by SubmissionId
        var existingSubmission = await _context.Submissions
            .FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, cancellationToken);
        if (existingSubmission == null)
        {
            _logger.LogDebug("Creating Submission {SubmissionId}", submission.SubmissionId);
            // StorageKey should already be set by GradingPipeline with StudentId string
            // Format: "submission_{submissionId}:student_{studentId}"
            // If not set, use default format
            if (string.IsNullOrWhiteSpace(submission.StorageKey))
            {
                submission.StorageKey = $"submission_{submission.SubmissionId:N}";
            }
            submission.Status = "processing";
            submission.CreatedAt = DateTime.UtcNow;
            _context.Submissions.Add(submission);
            await _context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            _logger.LogDebug("Updating Submission {SubmissionId}", submission.SubmissionId);
            existingSubmission.ExamId = submission.ExamId;
            existingSubmission.ExaminerId = submission.ExaminerId;
            existingSubmission.StudentId = submission.StudentId;
            // Update StorageKey if it contains StudentId string (preserve StudentId string in StorageKey)
            if (!string.IsNullOrWhiteSpace(submission.StorageKey) && submission.StorageKey.Contains(":student_"))
            {
                existingSubmission.StorageKey = submission.StorageKey;
            }
            existingSubmission.Status = "processing";
            await _context.SaveChangesAsync(cancellationToken);

            // Clear old artifacts for this submission
            // Vector-only: no local signatures/fingerprints to clear
            var oldUnits = _context.CodeUnits.Where(x => x.SubmissionId == submission.SubmissionId);
            _context.CodeUnits.RemoveRange(oldUnits);
            var oldFiles = _context.CodeFiles.Where(x => x.SubmissionId == submission.SubmissionId);
            _context.CodeFiles.RemoveRange(oldFiles);
            await _context.SaveChangesAsync(cancellationToken);
        }

        // Create CodeFiles from project path (fresh for both create and update)
        _logger.LogDebug("Creating CodeFiles for submission {SubmissionId} from {ProjectPath}", submission.SubmissionId, projectPath);
        var newCodeFiles = GetCodeFilesFromProject(projectPath, submission.SubmissionId);
        _context.CodeFiles.AddRange(newCodeFiles);
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogDebug("Created {Count} CodeFiles", newCodeFiles.Count);
    }

    private List<CodeFile> GetCodeFilesFromProject(string projectPath, Guid submissionId)
    {
        var codeFiles = new List<CodeFile>();
        var allowedExtensions = new[] { ".cs", ".cshtml", ".cshtml.cs" };
        var includeRoots = new[] { "/pages/", "/areas/", "/views/", "/models/", "/controllers/", "/services/" };
        var excludeSegments = new[] { "/bin/", "/obj/", "/node_modules/", "/migrations/", "/tests/", "/test/", "/properties/", "/wwwroot/" };
        
        try
        {
            var allFiles = Directory.GetFiles(projectPath, "*.*", SearchOption.AllDirectories);
            
            foreach (var filePath in allFiles)
            {
                var relPath = Path.GetRelativePath(projectPath, filePath).Replace('\\', '/');
                var extension = Path.GetExtension(filePath);

                // Extension filter (Razor Pages + C# only)
                if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Exclude common non-code/project folders
                if (excludeSegments.Any(seg => relPath.Contains(seg, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Include only typical Razor Pages roots
                var lowerRel = "/" + relPath.ToLowerInvariant();
                if (!includeRoots.Any(root => lowerRel.Contains(root, StringComparison.Ordinal)))
                    continue;

                // Skip large files for performance
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxFileSizeBytes)
                    continue;

                var fileContent = File.ReadAllText(filePath);

                var codeFile = new CodeFile
                {
                    FileId = Guid.NewGuid(),
                    SubmissionId = submissionId,
                    RelPath = relPath,
                    Language = _codeParserService.DetectLanguage(relPath),
                    LineCount = fileContent.Split('\n').Length,
                    FileHash = ComputeHash(fileContent),
                    FileSize = fileInfo.Length,
                    CreatedAt = DateTime.UtcNow
                };

                codeFiles.Add(codeFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading files from project path {ProjectPath}", projectPath);
        }

        return codeFiles;
    }

    private byte[] ComputeHash(string content)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
    }
}

