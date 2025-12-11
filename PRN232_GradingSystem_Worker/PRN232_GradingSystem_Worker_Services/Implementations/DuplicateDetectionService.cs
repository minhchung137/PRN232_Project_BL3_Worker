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

        // C?U H?NH NGÝ?NG Ð?O VÃN
        private const double SimilarityThreshold = 0.85; // Gi?ng nhau >= 85% là ð?o vãn

        // GI?I H?N HI?U NÃNG
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
        // PH?N 1: TÁCH CODE & T?O VECTOR GI? (Preprocessing)
        // =================================================================================================
        public async Task ExtractCodeUnitsAsync(Submission submission, string projectPath, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Extracting code units for submission {SubmissionId}", submission.SubmissionId);

            // 1. D?n d?p d? li?u c? (ð? tránh trùng l?p khi ch?y l?i)
            var oldUnits = _context.CodeUnits.Where(u => u.SubmissionId == submission.SubmissionId);
            _context.CodeUnits.RemoveRange(oldUnits);

            var oldFiles = _context.CodeFiles.Where(f => f.SubmissionId == submission.SubmissionId);
            _context.CodeFiles.RemoveRange(oldFiles);

            await _context.SaveChangesAsync(cancellationToken);

            // 2. Quét file trong thý m?c Project
            var newCodeFiles = GetCodeFilesFromProject(projectPath, submission.SubmissionId);
            _context.CodeFiles.AddRange(newCodeFiles);
            await _context.SaveChangesAsync(cancellationToken);

            // 3. Tách t?ng hàm (Unit) và t?o Vector
            var totalUnits = 0;
            foreach (var codeFile in newCodeFiles)
            {
                var filePath = Path.Combine(projectPath, codeFile.RelPath);
                if (!File.Exists(filePath)) continue;

                var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken);

                // Dùng CodeParserService ð? tách hàm
                var units = _codeParserService.ExtractCodeUnits(fileContent, codeFile.RelPath, codeFile.Language)
                    .Take(MaxUnitsPerFile);

                foreach (var unitInfo in units)
                {
                    if (totalUnits >= MaxUnitsPerSubmission) break;

                    // Lýu CodeUnit
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
                        // ContentHash ð? b? xóa trong DB m?i nên không map ? ðây
                    };

                    _context.CodeUnits.Add(codeUnit);

                    // --- LOGIC T?O VECTOR GI? (QUAN TR?NG CHO LOCAL) ---
                    // V? không có AI th?t, ta t?o vector d?a trên ð?c ði?m chu?i
                    float[] fakeVector = GenerateFakeVector(unitInfo.Content);

                    // Lýu Embedding (Serialize m?ng float thành chu?i JSON)
                    var embedding = new CodeEmbedding
                    {
                        UnitId = codeUnit.UnitId,
                        SubmissionId = submission.SubmissionId,
                        ModelName = "FakeLocalModel",
                        EmbeddingDimension = fakeVector.Length,
                        Emb = JsonSerializer.Serialize(fakeVector), // Lýu vào c?t TEXT
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
        // PH?N 2: PHÁT HI?N Ð?O VÃN (Core Logic)
        // =================================================================================================
        public async Task<DuplicateDetectionResult> DetectDuplicatesAsync(Submission submission, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting detection for {SubmissionId}", submission.SubmissionId);

            // 1. L?y vector c?a bài hi?n t?i t? DB
            // Ch? l?y c?t c?n thi?t ð? ti?t ki?m RAM
            var currentVectors = await _context.CodeEmbeddings
                .AsNoTracking()
                .Where(x => x.SubmissionId == submission.SubmissionId)
                .Select(x => new { x.UnitId, x.Emb })
                .ToListAsync(cancellationToken);

            if (!currentVectors.Any())
                return new DuplicateDetectionResult { SubmissionId1 = submission.SubmissionId, IsDuplicate = false };

            // 2. L?y vector c?a T?T C? bài khác trong cùng Exam
            var otherCandidates = await _context.CodeEmbeddings
                .AsNoTracking()
                .Where(x => x.Submission.ExamId == submission.ExamId && x.SubmissionId != submission.SubmissionId)
                .Select(x => new { x.UnitId, x.SubmissionId, x.Emb })
                .ToListAsync(cancellationToken);

            double maxScore = 0;
            Guid bestMatchSubmissionId = Guid.Empty;
            var matches = new List<MatchResult>();

            // 3. V?ng l?p so sánh (Brute-force in Memory)
            // Lýu ?: V?i d? li?u l?n (>10k units), cách này s? ch?m. Nhýng ð? án th? tho?i mái.
            foreach (var curr in currentVectors)
            {
                // Deserialize vector bài m?nh
                float[] v1 = JsonSerializer.Deserialize<float[]>(curr.Emb);

                foreach (var other in otherCandidates)
                {
                    // Deserialize vector bài ngý?i khác
                    float[] v2 = JsonSerializer.Deserialize<float[]>(other.Emb);

                    // Tính ð? týõng ð?ng
                    double score = CalculateCosineSimilarity(v1, v2);

                    // N?u vý?t ngý?ng -> Lýu l?i v?t
                    if (score >= SimilarityThreshold)
                    {
                        // T?o MatchResult (Kh?p v?i DB m?i ð? xóa các c?t th?a)
                        matches.Add(new MatchResult
                        {
                            ExamId = submission.ExamId,
                            SrcUnitId = curr.UnitId,
                            TgtUnitId = other.UnitId,
                            Score = (decimal)score,
                            Detail = JsonSerializer.Serialize(new { Reason = "Local Vector Match", Score = score }),
                            CreatedAt = DateTime.UtcNow
                        });

                        // C?p nh?t ði?m cao nh?t toàn bài
                        if (score > maxScore)
                        {
                            maxScore = score;
                            bestMatchSubmissionId = other.SubmissionId;
                        }
                    }
                }
            }

            // 4. Lýu k?t qu? vào Database
            if (matches.Any())
            {
                // Lýu chi ti?t t?ng ðo?n code trùng
                _context.MatchResults.AddRange(matches);

                // L?y StudentId c?a bài b? trùng ð? lýu báo cáo
                var student2Id = Guid.Empty;
                if (bestMatchSubmissionId != Guid.Empty)
                {
                    var otherSub = await _context.Submissions
                        .Where(s => s.SubmissionId == bestMatchSubmissionId)
                        .Select(s => s.StudentId)
                        .FirstOrDefaultAsync(cancellationToken);
                    student2Id = otherSub;
                }

                // Lýu báo cáo t?ng quan (DuplicateDetection)
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
                    MatchedUnits = JsonSerializer.Serialize(matches.OrderByDescending(m => m.Score).Take(5).Select(m => m.Score)), // Lýu top 5 ði?m
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
        // CÁC HÀM H? TR? (HELPER FUNCTIONS)
        // =================================================================================================

        /// <summary>
        /// Tính Cosine Similarity gi?a 2 vector.
        /// Công th?c: (A . B) / (||A|| * ||B||)
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
        /// Nguyên t?c: Code gi?ng nhau -> Vector gi?ng nhau. Code khác nhau -> Vector khác nhau.
        /// </summary>
        private float[] GenerateFakeVector(string content)
        {
            if (string.IsNullOrEmpty(content)) return new float[5];

            // Rút g?n code (b? kho?ng tr?ng th?a) ð? tính chính xác hõn
            var clean = content.Replace(" ", "").Replace("\r", "").Replace("\n", "");
            float len = clean.Length;

            // T?o vector 5 chi?u d?a trên các ð?c ði?m th?ng kê ðõn gi?n
            return new float[]
            {
                len % 100,                                  // Ð?c trýng 1: Ð? dài
                clean.Count(c => c == ';') % 20,            // Ð?c trýng 2: S? d?u ch?m ph?y
                clean.Count(c => c == '{') % 10,            // Ð?c trýng 3: S? kh?i l?nh
                (float)clean.Select(c => (int)c).Sum() % 500, // Ð?c trýng 4: T?ng m? ASCII
                clean.Contains("for") || clean.Contains("if") ? 1f : 0f // Ð?c trýng 5: T? khóa
            };
        }

        public async Task PrepareSubmissionAsync(Submission submission, string projectPath, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Preparing submission {SubmissionId}", submission.SubmissionId);

            // Ki?m tra Exam (n?u c?n thi?t, b? qua n?u tin tý?ng d? li?u)
            if (submission.ExamId != Guid.Empty)
            {
                // 1. Ki?m tra xem ExamId này ð? có trong DB c?a Worker chýa
                var examExists = await _context.Exams
                    .AnyAsync(e => e.ExamId == submission.ExamId, cancellationToken);

                // 2. N?u chýa có -> T?o m?t Exam "gi?" ð? th?a m?n khóa ngo?i
                // (V? Worker ch? c?n ID ð? gom nhóm, không quan tâm tên k? thi)
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
                    await _context.SaveChangesAsync(cancellationToken); // Lýu ngay ð? d?ng dý?i dùng ðý?c

                    _logger.LogWarning("Auto-created missing Exam {Id} to prevent FK error.", submission.ExamId);
                }
            }

            // B? sung thêm: Ki?m tra Examiner (Ngý?i ch?m) týõng t?
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
                // Gi? nguyên các thông tin khác
            }
            await _context.SaveChangesAsync(cancellationToken);

            // Xóa d? li?u rác c? n?u có
            // ... (Logic này ð? ðý?c x? l? ? ExtractCodeUnitsAsync nên có th? b? qua ? ðây cho g?n)
        }

        private List<CodeFile> GetCodeFilesFromProject(string projectPath, Guid submissionId)
        {
            var codeFiles = new List<CodeFile>();
            var allowedExtensions = new[] { ".cs" }; // Ch? l?y file C#
            var excludeSegments = new[] { "\\bin\\", "\\obj\\", "\\.git\\", "\\Migrations\\" }; // B? thý m?c rác

            try
            {
                if (!Directory.Exists(projectPath)) return codeFiles;

                var allFiles = Directory.GetFiles(projectPath, "*.*", SearchOption.AllDirectories);

                foreach (var filePath in allFiles)
                {
                    // Filter ðuôi file
                    var extension = Path.GetExtension(filePath);
                    if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                    // Filter thý m?c rác
                    if (excludeSegments.Any(seg => filePath.Contains(seg, StringComparison.OrdinalIgnoreCase))) continue;

                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > MaxFileSizeBytes) continue; // B? file quá l?n

                    var relPath = Path.GetRelativePath(projectPath, filePath).Replace('\\', '/');
                    var content = File.ReadAllText(filePath);

                    codeFiles.Add(new CodeFile
                    {
                        FileId = Guid.NewGuid(),
                        SubmissionId = submissionId,
                        RelPath = relPath,
                        Language = "csharp",
                        LineCount = content.Split('\n').Length,
                        // FileHash: B? qua ho?c dùng MD5 n?u b?ng DB có c?t này (DB m?i ð? xóa byte[])
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

        // Các hàm Interface th?a (c?a b?n c?) - Ð? tr?ng ð? th?a m?n Interface
        public Task CalculateFingerprintsAsync(Submission submission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CalculateSignaturesAsync(Submission submission, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}