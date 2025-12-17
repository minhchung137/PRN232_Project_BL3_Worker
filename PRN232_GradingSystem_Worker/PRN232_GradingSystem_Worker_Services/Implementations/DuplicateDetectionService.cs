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

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    public sealed class DuplicateDetectionService : IDuplicateDetectionService
    {
        private readonly WorkerDbContext _context;
        private readonly CodeParserService _codeParserService;
        private readonly ILogger<DuplicateDetectionService> _logger;

        // C?U H?NH NG�?NG �?O V�N
        private const double SimilarityThreshold = 0.85; // Gi?ng nhau >= 85% l� �?o v�n

        // GI?I H?N HI?U N�NG
        private const int MaxUnitsPerFile = 150;
        private const int MaxUnitsPerSubmission = 4000;
        private const long MaxFileSizeBytes = 200_000; // ~200KB

        public DuplicateDetectionService(
            WorkerDbContext context,
            CodeParserService codeParserService,
            ILogger<DuplicateDetectionService> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _codeParserService = codeParserService ?? throw new ArgumentNullException(nameof(codeParserService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // =================================================================================================
        // PH?N 1: T�CH CODE & T?O VECTOR GI? (Preprocessing)
        // =================================================================================================
        public async Task ExtractCodeUnitsAsync(Submission submission, string projectPath, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Extracting code units for submission {SubmissionId}", submission.SubmissionId);

            // 1. D?n d?p d? li?u c? (?? tr?nh tr?ng l?p khi ch?y l?i)
            // QUAN TR?NG: X?a theo th? t? ?? tr?nh vi ph?m foreign key constraint
            // - X?a CodeEmbeddings tr??c (child table, c? FK ??n CodeUnits)
            // - Sau ?? x?a CodeUnits (parent table c?a CodeEmbeddings)
            // - Cu?i c?ng x?a CodeFiles (parent table c?a CodeUnits)
            
            var oldEmbeddings = _context.CodeEmbeddings.Where(e => e.SubmissionId == submission.SubmissionId);
            _context.CodeEmbeddings.RemoveRange(oldEmbeddings);
            
            var oldUnits = _context.CodeUnits.Where(u => u.SubmissionId == submission.SubmissionId);
            _context.CodeUnits.RemoveRange(oldUnits);

            var oldFiles = _context.CodeFiles.Where(f => f.SubmissionId == submission.SubmissionId);
            _context.CodeFiles.RemoveRange(oldFiles);

            await _context.SaveChangesAsync(cancellationToken);

            // 2. Qu�t file trong th� m?c Project
            var newCodeFiles = GetCodeFilesFromProject(projectPath, submission.SubmissionId);
            _context.CodeFiles.AddRange(newCodeFiles);
            await _context.SaveChangesAsync(cancellationToken);

            // 3. T�ch t?ng h�m (Unit) v� t?o Vector
            var totalUnits = 0;
            foreach (var codeFile in newCodeFiles)
            {
                var filePath = Path.Combine(projectPath, codeFile.RelPath);
                if (!File.Exists(filePath)) continue;

                var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken);

                // D�ng CodeParserService �? t�ch h�m
                var units = _codeParserService.ExtractCodeUnits(fileContent, codeFile.RelPath, codeFile.Language)
                    .Take(MaxUnitsPerFile);

                foreach (var unitInfo in units)
                {
                    if (totalUnits >= MaxUnitsPerSubmission) break;

                    // L�u CodeUnit
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
                        CreatedAt = DateTime.UtcNow
                        // ContentHash �? b? x�a trong DB m?i n�n kh�ng map ? ��y
                    };

                    _context.CodeUnits.Add(codeUnit);

                    // --- LOGIC T?O VECTOR GI? (QUAN TR?NG CHO LOCAL) ---
                    // V? kh�ng c� AI th?t, ta t?o vector d?a tr�n �?c �i?m chu?i
                    float[] fakeVector = GenerateFakeVector(unitInfo.Content);

                    // L�u Embedding (Serialize m?ng float th�nh chu?i JSON)
                    var embedding = new CodeEmbedding
                    {
                        UnitId = codeUnit.UnitId,
                        SubmissionId = submission.SubmissionId,
                        ModelName = "FakeLocalModel",
                        EmbeddingDimension = fakeVector.Length,
                        Emb = JsonSerializer.Serialize(fakeVector), // L�u v�o c?t TEXT
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.CodeEmbeddings.Add(embedding);

                    totalUnits++;
                }
            }
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Extracted {Count} units for submission {Id}", totalUnits, submission.SubmissionId);
        }

        // =================================================================================================
        // PH?N 2: PH�T HI?N �?O V�N (Core Logic)
        // =================================================================================================
        public async Task<DuplicateDetectionResult> DetectDuplicatesAsync(Submission submission, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting detection for {SubmissionId}", submission.SubmissionId);

            // 1. L?y vector c?a b�i hi?n t?i t? DB
            // Ch? l?y c?t c?n thi?t �? ti?t ki?m RAM
            var currentVectors = await _context.CodeEmbeddings
                .AsNoTracking()
                .Where(x => x.SubmissionId == submission.SubmissionId)
                .Select(x => new { x.UnitId, x.Emb })
                .ToListAsync(cancellationToken);

            if (!currentVectors.Any())
                return new DuplicateDetectionResult { SubmissionId1 = submission.SubmissionId, IsDuplicate = false };

            // 2. L?y vector c?a T?T C? b�i kh�c trong c�ng Exam
            var otherCandidates = await _context.CodeEmbeddings
                .AsNoTracking()
                .Where(x => x.Submission.ExamId == submission.ExamId && x.SubmissionId != submission.SubmissionId)
                .Select(x => new { x.UnitId, x.SubmissionId, x.Emb })
                .ToListAsync(cancellationToken);

            double maxScore = 0;
            Guid bestMatchSubmissionId = Guid.Empty;
            var matches = new List<MatchResult>();

            // 3. V?ng l?p so s�nh (Brute-force in Memory)
            // L�u ?: V?i d? li?u l?n (>10k units), c�ch n�y s? ch?m. Nh�ng �? �n th? tho?i m�i.
            foreach (var curr in currentVectors)
            {
                // Deserialize vector b�i m?nh
                float[] v1 = JsonSerializer.Deserialize<float[]>(curr.Emb);

                foreach (var other in otherCandidates)
                {
                    // Deserialize vector b�i ng�?i kh�c
                    float[] v2 = JsonSerializer.Deserialize<float[]>(other.Emb);

                    // T�nh �? t��ng �?ng
                    double score = CalculateCosineSimilarity(v1, v2);

                    // N?u v�?t ng�?ng -> L�u l?i v?t
                    if (score >= SimilarityThreshold)
                    {
                        // T?o MatchResult (Kh?p v?i DB m?i �? x�a c�c c?t th?a)
                        matches.Add(new MatchResult
                        {
                            ExamId = submission.ExamId,
                            SrcUnitId = curr.UnitId,
                            TgtUnitId = other.UnitId,
                            Score = (decimal)score,
                            Detail = JsonSerializer.Serialize(new { Reason = "Local Vector Match", Score = score }),
                            CreatedAt = DateTime.UtcNow
                        });

                        // C?p nh?t �i?m cao nh?t to�n b�i
                        if (score > maxScore)
                        {
                            maxScore = score;
                            bestMatchSubmissionId = other.SubmissionId;
                        }
                    }
                }
            }

            // 4. L�u k?t qu? v�o Database
            if (matches.Any())
            {
                // L�u chi ti?t t?ng �o?n code tr�ng
                _context.MatchResults.AddRange(matches);

                // L?y StudentId c?a b�i b? tr�ng �? l�u b�o c�o
                var student2Id = Guid.Empty;
                if (bestMatchSubmissionId != Guid.Empty)
                {
                    var otherSub = await _context.Submissions
                        .Where(s => s.SubmissionId == bestMatchSubmissionId)
                        .Select(s => s.StudentId)
                        .FirstOrDefaultAsync(cancellationToken);
                    student2Id = otherSub;
                }

                // L�u b�o c�o t?ng quan (DuplicateDetection)
                var resultDb = new DuplicateDetection
                {
                    ExamId = submission.ExamId,
                    ExaminerId = submission.ExaminerId,
                    SubmissionId1 = submission.SubmissionId,
                    SubmissionId2 = bestMatchSubmissionId,
                    StudentId1 = submission.StudentId,
                    StudentId2 = student2Id,
                    VectorScore = (decimal)maxScore,
                    IsDuplicate = true,
                    ThresholdUsed = $"Similarity >= {SimilarityThreshold}",
                    MatchedUnits = JsonSerializer.Serialize(matches.OrderByDescending(m => m.Score).Take(5).Select(m => m.Score)), // L�u top 5 �i?m
                    Notes = "Detected by Local Logic (No-Vector DB)",
                    CreatedAt = DateTime.UtcNow
                };

                _context.DuplicateDetections.Add(resultDb);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Found duplicate with score {Score}", maxScore);

                return new DuplicateDetectionResult
                {
                    SubmissionId1 = submission.SubmissionId,
                    SubmissionId2 = bestMatchSubmissionId,
                    IsDuplicate = true,
                    VectorScore = (decimal)maxScore,
                    OverallScore = (decimal)maxScore
                };
            }

            _logger.LogInformation("No duplicate found.");
            return new DuplicateDetectionResult { SubmissionId1 = submission.SubmissionId, IsDuplicate = false };
        }

        // =================================================================================================
        // C�C H�M H? TR? (HELPER FUNCTIONS)
        // =================================================================================================

        /// <summary>
        /// T�nh Cosine Similarity gi?a 2 vector.
        /// C�ng th?c: (A . B) / (||A|| * ||B||)
        /// </summary>
        private double CalculateCosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length) return 0;

            double dot = 0.0, mag1 = 0.0, mag2 = 0.0;
            for (int i = 0; i < v1.Length; i++)
            {
                dot += v1[i] * v2[i];
                mag1 += v1[i] * v1[i];
                mag2 += v2[i] * v2[i];
            }

            if (mag1 == 0 || mag2 == 0) return 0;
            return dot / (Math.Sqrt(mag1) * Math.Sqrt(mag2));
        }

        /// <summary>
        /// T?o Vector gi? t? n?i dung code.
        /// Nguy�n t?c: Code gi?ng nhau -> Vector gi?ng nhau. Code kh�c nhau -> Vector kh�c nhau.
        /// </summary>
        private float[] GenerateFakeVector(string content)
        {
            if (string.IsNullOrEmpty(content)) return new float[5];

            // R�t g?n code (b? kho?ng tr?ng th?a) �? t�nh ch�nh x�c h�n
            var clean = content.Replace(" ", "").Replace("\r", "").Replace("\n", "");
            float len = clean.Length;

            // T?o vector 5 chi?u d?a tr�n c�c �?c �i?m th?ng k� ��n gi?n
            return new float[]
            {
                len % 100,                                  // �?c tr�ng 1: �? d�i
                clean.Count(c => c == ';') % 20,            // �?c tr�ng 2: S? d?u ch?m ph?y
                clean.Count(c => c == '{') % 10,            // �?c tr�ng 3: S? kh?i l?nh
                (float)clean.Select(c => (int)c).Sum() % 500, // �?c tr�ng 4: T?ng m? ASCII
                clean.Contains("for") || clean.Contains("if") ? 1f : 0f // �?c tr�ng 5: T? kh�a
            };
        }

        public async Task PrepareSubmissionAsync(Submission submission, string projectPath, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Preparing submission {SubmissionId}", submission.SubmissionId);

            // Ki?m tra Exam (n?u c?n thi?t, b? qua n?u tin t�?ng d? li?u)
            if (submission.ExamId != Guid.Empty)
            {
                // 1. Ki?m tra xem ExamId n�y �? c� trong DB c?a Worker ch�a
                var examExists = await _context.Exams
                    .AnyAsync(e => e.ExamId == submission.ExamId, cancellationToken);

                // 2. N?u ch�a c� -> T?o m?t Exam "gi?" �? th?a m?n kh�a ngo?i
                // (V? Worker ch? c?n ID �? gom nh�m, kh�ng quan t�m t�n k? thi)
                if (!examExists)
                {
                    var dummyExam = new Exam
                    {
                        ExamId = submission.ExamId,
                        Code = "AUTO_" + submission.ExamId.ToString().Substring(0, 8), // M? t?m
                        Title = "Auto Generated Exam (Missing Metadata)",
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Exams.Add(dummyExam);
                    await _context.SaveChangesAsync(cancellationToken); // L�u ngay �? d?ng d�?i d�ng ��?c

                    _logger.LogWarning("Auto-created missing Exam {Id} to prevent FK error.", submission.ExamId);
                }
            }

            // B? sung th�m: Ki?m tra Examiner (Ng�?i ch?m) t��ng t?
            if (submission.ExaminerId.HasValue && submission.ExaminerId.Value != Guid.Empty)
            {
                var examinerExists = await _context.Examiners
                    .AnyAsync(e => e.ExaminerId == submission.ExaminerId.Value, cancellationToken);

                if (!examinerExists)
                {
                    var dummyExaminer = new Examiner
                    {
                        ExaminerId = submission.ExaminerId.Value,
                        Code = "AUTO_WORKER",
                        Name = "Auto Generated Examiner",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Examiners.Add(dummyExaminer);
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }

            // Upsert Submission (T?o ho?c C?p nh?t)
            var existingSubmission = await _context.Submissions
                .FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, cancellationToken);

            if (existingSubmission == null)
            {
                if (string.IsNullOrWhiteSpace(submission.StorageKey))
                    submission.StorageKey = $"submission_{submission.SubmissionId:N}";

                submission.Status = "processing";
                submission.CreatedAt = DateTime.UtcNow;
                _context.Submissions.Add(submission);
            }
            else
            {
                existingSubmission.Status = "processing";
                // Gi? nguy�n c�c th�ng tin kh�c
            }
            await _context.SaveChangesAsync(cancellationToken);

            // X�a d? li?u r�c c? n?u c�
            // ... (Logic n�y �? ��?c x? l? ? ExtractCodeUnitsAsync n�n c� th? b? qua ? ��y cho g?n)
        }

        private List<CodeFile> GetCodeFilesFromProject(string projectPath, Guid submissionId)
        {
            var codeFiles = new List<CodeFile>();
            var allowedExtensions = new[] { ".cs" }; // Ch? l?y file C#
            var excludeSegments = new[] { "\\bin\\", "\\obj\\", "\\.git\\", "\\Migrations\\" }; // B? th� m?c r�c

            try
            {
                if (!Directory.Exists(projectPath)) return codeFiles;

                var allFiles = Directory.GetFiles(projectPath, "*.*", SearchOption.AllDirectories);

                foreach (var filePath in allFiles)
                {
                    // Filter �u�i file
                    var extension = Path.GetExtension(filePath);
                    if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                    // Filter th� m?c r�c
                    if (excludeSegments.Any(seg => filePath.Contains(seg, StringComparison.OrdinalIgnoreCase))) continue;

                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > MaxFileSizeBytes) continue; // B? file qu� l?n

                    var relPath = Path.GetRelativePath(projectPath, filePath).Replace('\\', '/');
                    var content = File.ReadAllText(filePath);

                    codeFiles.Add(new CodeFile
                    {
                        FileId = Guid.NewGuid(),
                        SubmissionId = submissionId,
                        RelPath = relPath,
                        Language = "csharp",
                        LineCount = content.Split('\n').Length,
                        // FileHash: B? qua ho?c d�ng MD5 n?u b?ng DB c� c?t n�y (DB m?i �? x�a byte[])
                        FileSize = fileInfo.Length,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading files");
            }
            return codeFiles;
        }

        // C�c h�m Interface th?a (c?a b?n c?) - �? tr?ng �? th?a m?n Interface
        public Task CalculateFingerprintsAsync(Submission submission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CalculateSignaturesAsync(Submission submission, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}