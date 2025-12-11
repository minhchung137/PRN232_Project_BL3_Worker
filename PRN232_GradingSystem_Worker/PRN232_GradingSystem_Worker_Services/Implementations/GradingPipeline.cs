using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PRN232_GradingSystem_Worker_Repo.DBContext;
using PRN232_GradingSystem_Worker_Repo.Models;
using PRN232_GradingSystem_Worker_Services.Interfaces;
using PRN232_GradingSystem_Worker_Services.Models;
using PRN232_GradingSystem_Worker_Services.Settings;

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    public sealed class GradingPipeline : IGradingPipeline
    {
        private readonly IFileDownloadService _fileDownloadService;
        private readonly IDbResetService _dbResetService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ProcessTrackerService _processTracker;
        private readonly ILogger<GradingPipeline> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _workingDirectory;
        private readonly string _resetScript;
        private readonly ValidationSettings _validationSettings;
        
        // Dictionary to store StudentId string (from RabbitMQ) mapped to SubmissionId
        // This allows us to retrieve the original StudentId string when duplicate is detected
        // Note: This is a temporary in-memory solution. For production, consider storing StudentId string in DB.
        private static readonly Dictionary<Guid, string> _submissionStudentIdMap = new Dictionary<Guid, string>();
        
        // Dictionary to store StudentId string mapped to StudentId GUID (for reverse lookup)
        // This helps find StudentId string when we only have the GUID
        private static readonly Dictionary<Guid, string> _studentIdGuidToStringMap = new Dictionary<Guid, string>();

        public GradingPipeline(
            IFileDownloadService fileDownloadService,
            IDbResetService dbResetService,
            IServiceScopeFactory serviceScopeFactory,
            ProcessTrackerService processTracker,
            ILogger<GradingPipeline> logger,
            IConfiguration configuration,
            string workingDirectory,
            string resetScript)
        {
            _fileDownloadService = fileDownloadService ?? throw new ArgumentNullException(nameof(fileDownloadService));
            _dbResetService = dbResetService ?? throw new ArgumentNullException(nameof(dbResetService));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _processTracker = processTracker ?? throw new ArgumentNullException(nameof(processTracker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
            _resetScript = resetScript ?? throw new ArgumentNullException(nameof(resetScript));
            _validationSettings = _configuration.GetSection(ValidationSettings.SectionName).Get<ValidationSettings>()
                ?? new ValidationSettings();
        }

        /// <summary>
        /// Creates a TestResultDetail with all scores = 0 and empty notes (for validation failures)
        /// Note: The validation failure message should be set in GradingResult.Note and passed as Comment to callback API
        /// </summary>
        private TestResultDetail CreateZeroScoreTestResultDetail(string note)
        {
            // Note: All Note fields are left empty - the validation failure message will be in Comment field of request
            return new TestResultDetail
            {
                // Q1: Login (1.0 point)
                Q1Login = 0.0,
                Q1LoginNote = string.Empty,

                // Q2: ListAll (0.25), ListAll2 (0.25), Pagging (1.0)
                Q2ListAll = 0.0,
                Q2ListAllNote = string.Empty,
                Q2ListAll2 = 0.0,
                Q2ListAll2Note = string.Empty,
                Q2Pagging = 0.0,
                Q2PaggingNote = string.Empty,

                // Q3: Add Ok (1.0), Display Top (0.25), Validation Combobox (0.25), Validation Required (0.25), Validation - characters length (0.25), Validation - No special characters (0.5)
                Q3AddOk = 0.0,
                Q3AddOkNote = string.Empty,
                Q3DisplayTop = 0.0,
                Q3DisplayTopNote = string.Empty,
                Q3ValidationCombobox = 0.0,
                Q3ValidationComboboxNote = string.Empty,
                Q3ValidationRequired = 0.0,
                Q3ValidationRequiredNote = string.Empty,
                Q3ValidationLength = 0.0,
                Q3ValidationLengthNote = string.Empty,
                Q3ValidationSpecialCharacters = 0.0,
                Q3ValidationSpecialCharactersNote = string.Empty,

                // Q4: Update OK (1.0), Update Validation (1.0)
                Q4UpdateOk = 0.0,
                Q4UpdateOkNote = string.Empty,
                Q4UpdateValidation = 0.0,
                Q4UpdateValidationNote = string.Empty,

                // Q5: Test 1 (0.5), Test 2 (0.5), Test 3 (0.5)
                Q5Test1 = 0.0,
                Q5Test1Note = string.Empty,
                Q5Test2 = 0.0,
                Q5Test2Note = string.Empty,
                Q5Test3 = 0.0,
                Q5Test3Note = string.Empty,

                // Q6: Delete with SignalR (1.5)
                Q6DeleteWithSignalR = 0.0,
                Q6DeleteWithSignalRNote = string.Empty
            };
        }

        public async Task<GradingResult> ProcessSubmissionAsync(
            string submissionId,
            string fileUrl,
            string? examCode,
            string? studentId,
            string? examinerCode,
            CancellationToken cancellationToken)
        {
            var result = new GradingResult();
            var startTime = DateTime.UtcNow;
            var stepLogs = new List<GradingStepLog>();

            // Resolve Exam/Examiner IDs from codes
            Submission? submission = null;
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var duplicateDetectionService = scope.ServiceProvider.GetRequiredService<IDuplicateDetectionService>();
                var context = scope.ServiceProvider.GetRequiredService<PRN232_Grading_System_GradingContext>();
                
                submission = await ResolveSubmissionAsync(context, submissionId, examCode, studentId, examinerCode, cancellationToken);
            }
            catch (Exception ex)
            {
                stepLogs.Add(new GradingStepLog { Step = "Validation", Message = $"Failed to resolve submission: {ex.Message}", Level = "Error" });
                result.Success = false;
                result.AutoScore = 0;
                result.Note = $"Failed to resolve submission: {ex.Message}";
                result.StepLogs = stepLogs;
                return result;
            }

            try
            {
                // Step 1: Download ZIP file
                stepLogs.Add(new GradingStepLog { Step = "Download", Message = $"Downloading submission {submissionId} from {fileUrl}" });
                var zipPath = await DownloadSubmissionAsync(submissionId, fileUrl, cancellationToken);
                stepLogs.Add(new GradingStepLog { Step = "Download", Message = $"Downloaded to {zipPath}" });

                // Step 2: Extract ZIP
                stepLogs.Add(new GradingStepLog { Step = "Extract", Message = "Extracting ZIP file" });
                var projectPath = await ExtractZipAsync(zipPath, cancellationToken);
                // Remove build artifacts (bin/obj) to avoid locked DLLs from student ZIPs
                RemoveBuildArtifacts(projectPath);
                stepLogs.Add(new GradingStepLog { Step = "Extract", Message = $"Extracted to {projectPath}" });

                // Step 3: Reset Database (before validation/build)
                stepLogs.Add(new GradingStepLog { Step = "Database", Message = "Resetting database" });
                try
                {
                    await _dbResetService.ResetAsync(_resetScript, cancellationToken);
                    stepLogs.Add(new GradingStepLog { Step = "Database", Message = "Database reset completed" });
                }
                catch (Exception ex)
                {
                    // Do not fail the entire grading due to reset issues; log and continue
                    stepLogs.Add(new GradingStepLog { Step = "Database", Message = $"Database reset failed: {ex.Message}", Level = "Warning" });
                }

                                // Step 4: Static Analysis disabled per request
                // Zero-score violation checks (fail-fast before heavy steps)
                stepLogs.Add(new GradingStepLog { Step = "Validation", Message = "Running zero-score violation checks" });
                // 1) N-layer structure (>=3 projects) and solution naming
                // Check for .sln file first - if not found, skip build/run/test and return 0 score with normal format
                var solutionFile = FindSolutionFile(projectPath);
                bool hasSolutionFile = solutionFile != null;
                
                if (!hasSolutionFile)
                {
                    _logger.LogWarning("[GradingPipeline] No .sln file found in project. Skipping build/run/test, returning 0 score with normal format.");
                    stepLogs.Add(new GradingStepLog { Step = "Validation", Message = "No .sln file found - skipping build/run/test", Level = "Error" });
                    
                    // Log test results in the same format as normal grading (mimicking PlaywrightTestService output)
                    _logger.LogInformation("[Playwright] Step 7: Running UI tests with Playwright...");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0");
                    _logger.LogInformation("[Playwright] Q2 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q3 Score: 0/2.5");
                    _logger.LogInformation("[Playwright] Q4 Score: 0/2.0");
                    _logger.LogInformation("[Playwright] Q5 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] ========================================");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0 | Q2 Score: 0/1.5 | Q3 Score: 0/2.5 | Q4 Score: 0/2.0 | Q5 Score: 0/1.5 | Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Total Score: 0/10.0");
                    _logger.LogInformation("[Playwright] ========================================");
                    
                    // Skip to create result with 0 scores but normal format
                    result.Success = true;
                    result.AutoScore = 0;
                    result.TotalTests = 6; // Q1-Q6
                    result.PassedTests = 0;
                    result.Note = "No solution file (.sln) found. All tests scored 0.";
                    result.StepLogs = stepLogs;
                    result.Duration = DateTime.UtcNow - startTime;
                    result.TestResultDetail = CreateZeroScoreTestResultDetail("Not Found project in solution");
                    stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Tests skipped: 0/6 passed (No .sln file found)" });
                    return result;
                }
                
                var nLayerValidation = await ValidateNLayerStructureAsync(projectPath);
                if (!nLayerValidation.IsValid)
                {
                    // Log test results in the same format as normal grading
                    _logger.LogInformation("[Playwright] Step 7: Running UI tests with Playwright...");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0");
                    _logger.LogInformation("[Playwright] Q2 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q3 Score: 0/2.5");
                    _logger.LogInformation("[Playwright] Q4 Score: 0/2.0");
                    _logger.LogInformation("[Playwright] Q5 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] ========================================");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0 | Q2 Score: 0/1.5 | Q3 Score: 0/2.5 | Q4 Score: 0/2.0 | Q5 Score: 0/1.5 | Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Total Score: 0/10.0");
                    _logger.LogInformation("[Playwright] ========================================");
                    
                    result.Success = true;
                    result.AutoScore = 0;
                    result.TotalTests = 6;
                    result.PassedTests = 0;
                    // Include detailed violation information
                    result.Note = $"Violation: {nLayerValidation.Details}";
                    result.TestResultDetail = CreateZeroScoreTestResultDetail(nLayerValidation.Details);
                    stepLogs.Add(new GradingStepLog { Step = "Validation", Message = "Invalid N-layer structure or solution name", Level = "Error" });
                    stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Tests skipped: 0/6 passed (Validation failed)" });
                    result.StepLogs = stepLogs;
                    result.Duration = DateTime.UtcNow - startTime;
                    return result;
                }
                // 2) Connection string location (no hardcoded in DbContext; exists in appsettings)
                if (await CheckHardcodedConnectionStringsAsync(projectPath))
                {
                    // Get detailed violation message with line numbers and file paths
                    var violationDetails = await GetHardcodedConnectionStringDetailsAsync(projectPath);
                    var note = string.IsNullOrEmpty(violationDetails) 
                        ? "Hardcoded connection string detected in DbContext" 
                        : $"Hardcoded connection string detected: {violationDetails}";
                    
                    // Log test results in the same format as normal grading
                    _logger.LogInformation("[Playwright] Step 7: Running UI tests with Playwright...");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0");
                    _logger.LogInformation("[Playwright] Q2 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q3 Score: 0/2.5");
                    _logger.LogInformation("[Playwright] Q4 Score: 0/2.0");
                    _logger.LogInformation("[Playwright] Q5 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] ========================================");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0 | Q2 Score: 0/1.5 | Q3 Score: 0/2.5 | Q4 Score: 0/2.0 | Q5 Score: 0/1.5 | Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Total Score: 0/10.0");
                    _logger.LogInformation("[Playwright] ========================================");
                    
                    result.Success = true;
                    result.AutoScore = 0;
                    result.TotalTests = 6;
                    result.PassedTests = 0;
                    // Include detailed violation information (file path and line number)
                    result.Note = $"Violation: {note}";
                    result.TestResultDetail = CreateZeroScoreTestResultDetail(note);
                    stepLogs.Add(new GradingStepLog { Step = "Validation", Message = "Hardcoded connection string detected", Level = "Error" });
                    stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Tests skipped: 0/6 passed (Validation failed)" });
                    result.StepLogs = stepLogs;
                    result.Duration = DateTime.UtcNow - startTime;
                    return result;
                }
                // 3) Basic project structure (appsettings/Program.cs/.sln present)
                var projectStructureValidation = await ValidateProjectStructureAsync(projectPath);
                if (!projectStructureValidation.IsValid)
                {
                    // Log test results in the same format as normal grading
                    _logger.LogInformation("[Playwright] Step 7: Running UI tests with Playwright...");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0");
                    _logger.LogInformation("[Playwright] Q2 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q3 Score: 0/2.5");
                    _logger.LogInformation("[Playwright] Q4 Score: 0/2.0");
                    _logger.LogInformation("[Playwright] Q5 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] ========================================");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0 | Q2 Score: 0/1.5 | Q3 Score: 0/2.5 | Q4 Score: 0/2.0 | Q5 Score: 0/1.5 | Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Total Score: 0/10.0");
                    _logger.LogInformation("[Playwright] ========================================");
                    
                    result.Success = true;
                    result.AutoScore = 0;
                    result.TotalTests = 6;
                    result.PassedTests = 0;
                    // Include detailed violation information
                    result.Note = $"Violation: {projectStructureValidation.Details}";
                    result.TestResultDetail = CreateZeroScoreTestResultDetail(projectStructureValidation.Details);
                    stepLogs.Add(new GradingStepLog { Step = "Validation", Message = "Invalid project structure", Level = "Error" });
                    stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Tests skipped: 0/6 passed (Validation failed)" });
                    result.StepLogs = stepLogs;
                    result.Duration = DateTime.UtcNow - startTime;
                    return result;
                }
                // 4) Forbidden keywords (e.g., 'Lion')
                var keywordCheck = await CheckRequiredKeywordsAsync(projectPath);
                if (!keywordCheck.HasRequiredKeywords)
                {
                    // Log test results in the same format as normal grading
                    _logger.LogInformation("[Playwright] Step 7: Running UI tests with Playwright...");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0");
                    _logger.LogInformation("[Playwright] Q2 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q3 Score: 0/2.5");
                    _logger.LogInformation("[Playwright] Q4 Score: 0/2.0");
                    _logger.LogInformation("[Playwright] Q5 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] ========================================");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0 | Q2 Score: 0/1.5 | Q3 Score: 0/2.5 | Q4 Score: 0/2.0 | Q5 Score: 0/1.5 | Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Total Score: 0/10.0");
                    _logger.LogInformation("[Playwright] ========================================");
                    
                    // Format note with violation details (already contains line numbers and file paths)
                    var keywordNote = keywordCheck.MissingKeywords;
                    if (string.IsNullOrEmpty(keywordNote))
                    {
                        keywordNote = "Found violation keyword";
                    }
                    else
                    {
                        // Format: "Found violation keyword: {keyword} at line {lineNumber} in {relativePath}"
                        keywordNote = keywordNote.Replace("Found forbidden keywords: ", "Found violation keyword: ");
                    }
                    
                    result.Success = true;
                    result.AutoScore = 0;
                    result.TotalTests = 6;
                    result.PassedTests = 0;
                    // Include detailed violation information (keyword, file path and line number)
                    result.Note = $"Violation: {keywordNote}";
                    result.TestResultDetail = CreateZeroScoreTestResultDetail(keywordNote);
                    stepLogs.Add(new GradingStepLog { Step = "Validation", Message = "Forbidden keyword found", Level = "Error" });
                    stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Tests skipped: 0/6 passed (Validation failed)" });
                    result.StepLogs = stepLogs;
                    result.Duration = DateTime.UtcNow - startTime;
                    return result;
                }

                // Step 4.5: Duplicate Detection
                _logger.LogInformation("[GradingPipeline] Step 4.5: Running duplicate detection");
                stepLogs.Add(new GradingStepLog { Step = "DuplicateDetection", Message = "Running duplicate detection" });
                try
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var duplicateDetectionService = scope.ServiceProvider.GetRequiredService<IDuplicateDetectionService>();
                    
                    // Store StudentId string in submission.StorageKey before PrepareSubmissionAsync
                    // Format: "submission_{submissionId}:student_{studentId}"
                    // This allows us to retrieve StudentId string later when duplicate is detected
                    if (!string.IsNullOrWhiteSpace(studentId))
                    {
                        // Always update StorageKey to include StudentId string (even if it was set before)
                        submission.StorageKey = $"submission_{submission.SubmissionId:N}:student_{studentId}";
                    }
                    
                    // Prepare submission: create Exam/Examiner/Submission/CodeFiles
                    await duplicateDetectionService.PrepareSubmissionAsync(submission, projectPath, cancellationToken);
                    stepLogs.Add(new GradingStepLog { Step = "DuplicateDetection", Message = "Submission prepared with Exam/Examiner/CodeFiles" });
                    
                    // Extract code units and signatures (vector-only mode)
                    await duplicateDetectionService.ExtractCodeUnitsAsync(submission, projectPath, cancellationToken);
                    await duplicateDetectionService.CalculateSignaturesAsync(submission, cancellationToken);
                    
                    // Detect duplicates (fail-fast: 0 điểm nếu trùng)
                    var duplicateResult = await duplicateDetectionService.DetectDuplicatesAsync(submission, cancellationToken);
                    if (duplicateResult.IsDuplicate)
                    {
                        // Get StudentId string of the duplicate submission
                        string duplicateStudentId = "Unknown";
                        string? studentIdFromMap = null;
                        lock (_submissionStudentIdMap)
                        {
                            if (_submissionStudentIdMap.TryGetValue(duplicateResult.SubmissionId2, out var studentIdStr))
                            {
                                studentIdFromMap = studentIdStr;
                            }
                        }
                        
                        if (studentIdFromMap != null)
                        {
                            duplicateStudentId = studentIdFromMap;
                        }
                        else
                        {
                            // If not in map, try to get from DB (if submission was created before)
                            try
                            {
                                using var dbScope = _serviceScopeFactory.CreateScope();
                                var dbContext = dbScope.ServiceProvider.GetRequiredService<PRN232_Grading_System_GradingContext>();
                                var duplicateSub = await dbContext.Submissions
                                    .FirstOrDefaultAsync(s => s.SubmissionId == duplicateResult.SubmissionId2, cancellationToken);
                                
                                // Try to get StudentId string from StorageKey or GUID mapping
                                if (duplicateSub != null)
                                {
                                    // Priority 1: Try to parse StudentId string from StorageKey
                                    // Format: "submission_{submissionId}:student_{studentId}"
                                    if (!string.IsNullOrWhiteSpace(duplicateSub.StorageKey) && duplicateSub.StorageKey.Contains(":student_"))
                                    {
                                        var parts = duplicateSub.StorageKey.Split(new[] { ":student_" }, StringSplitOptions.None);
                                        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                                        {
                                            duplicateStudentId = parts[1];
                                        }
                                    }
                                    
                                    // Priority 2: Try to get from GUID mapping (if StorageKey parse failed)
                                    if (duplicateStudentId == "Unknown")
                                    {
                                        lock (_studentIdGuidToStringMap)
                                        {
                                            if (_studentIdGuidToStringMap.TryGetValue(duplicateSub.StudentId, out var studentIdStr))
                                            {
                                                duplicateStudentId = studentIdStr;
                                            }
                                        }
                                    }
                                    
                                    // Priority 3: Fallback - if still "Unknown", try to reverse-engineer from GUID
                                    // This won't work perfectly, but we'll try to find matching StudentId string
                                    if (duplicateStudentId == "Unknown")
                                    {
                                        // Check if the GUID matches any StudentId string we've seen (by computing MD5 hash)
                                        lock (_studentIdGuidToStringMap)
                                        {
                                            foreach (var kvp in _studentIdGuidToStringMap)
                                            {
                                                if (string.IsNullOrWhiteSpace(kvp.Value)) continue;
                                                try
                                                {
                                                    using var md5 = MD5.Create();
                                                    var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(kvp.Value));
                                                    var guidBytes = new byte[16];
                                                    Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);
                                                    var computedGuid = new Guid(guidBytes);
                                                    if (computedGuid == duplicateSub.StudentId)
                                                    {
                                                        duplicateStudentId = kvp.Value;
                                                        break;
                                                    }
                                                }
                                                catch
                                                {
                                                    // Ignore errors
                                                }
                                            }
                                        }
                                    }
                                    
                                    // Final fallback: if still "Unknown", use GUID as string (should not happen if StorageKey is set correctly)
                                    if (duplicateStudentId == "Unknown")
                                    {
                                        duplicateStudentId = duplicateSub.StudentId.ToString();
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore errors, use "Unknown"
                            }
                        }
                        
                        var duplicateNote = $"Duplicate code detected with submission from student {duplicateStudentId}";
                        
                        // Log test results in the same format as normal grading
                        _logger.LogInformation("[Playwright] Step 7: Running UI tests with Playwright...");
                        _logger.LogInformation("[Playwright] Q1 Score: 0/1.0");
                        _logger.LogInformation("[Playwright] Q2 Score: 0/1.5");
                        _logger.LogInformation("[Playwright] Q3 Score: 0/2.5");
                        _logger.LogInformation("[Playwright] Q4 Score: 0/2.0");
                        _logger.LogInformation("[Playwright] Q5 Score: 0/1.5");
                        _logger.LogInformation("[Playwright] Q6 Score: 0/1.5");
                        _logger.LogInformation("[Playwright] ========================================");
                        _logger.LogInformation("[Playwright] Q1 Score: 0/1.0 | Q2 Score: 0/1.5 | Q3 Score: 0/2.5 | Q4 Score: 0/2.0 | Q5 Score: 0/1.5 | Q6 Score: 0/1.5");
                        _logger.LogInformation("[Playwright] Total Score: 0/10.0");
                        _logger.LogInformation("[Playwright] ========================================");
                        
                        stepLogs.Add(new GradingStepLog { Step = "DuplicateDetection", Message = $"Duplicate detected (vector) vs {duplicateResult.SubmissionId2}, Score: {duplicateResult.OverallScore:P}", Level = "Warning" });
                    result.Success = true;
                    result.AutoScore = 0;
                    result.TotalTests = 6;
                    result.PassedTests = 0;
                    // Use duplicateNote which contains student ID: "Duplicate code detected with submission from student {studentId}"
                    result.Note = duplicateNote;
                    result.TestResultDetail = CreateZeroScoreTestResultDetail(duplicateNote);
                        result.StepLogs = stepLogs;
                        result.Duration = DateTime.UtcNow - startTime;
                        stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Tests skipped: 0/6 passed (Duplicate code detected)" });
                        return result;
                    }
                    _logger.LogInformation("[GradingPipeline] Duplicate detection completed - No duplicates found");
                    stepLogs.Add(new GradingStepLog { Step = "DuplicateDetection", Message = "No duplicates found" });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GradingPipeline] Duplicate detection failed: {Message}", ex.Message);
                    stepLogs.Add(new GradingStepLog { Step = "DuplicateDetection", Message = $"Duplicate detection failed: {ex.Message}", Level = "Warning" });
                    // Don't fail the entire grading if duplicate detection fails
                }

                // Step 5: Build Project
                _logger.LogInformation("[GradingPipeline] Step 5: Building project...");
                stepLogs.Add(new GradingStepLog { Step = "Build", Message = "Building project" });
                var buildSuccess = await BuildProjectAsync(projectPath, cancellationToken);
                if (!buildSuccess)
                {
                    _logger.LogError("[GradingPipeline] Build failed");
                    stepLogs.Add(new GradingStepLog { Step = "Build", Message = "Build failed", Level = "Error" });

                    // Mimic Playwright output so callback receives consistent data
                    _logger.LogInformation("[Playwright] Step 7: Running UI tests with Playwright...");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0");
                    _logger.LogInformation("[Playwright] Q2 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q3 Score: 0/2.5");
                    _logger.LogInformation("[Playwright] Q4 Score: 0/2.0");
                    _logger.LogInformation("[Playwright] Q5 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] ========================================");
                    _logger.LogInformation("[Playwright] Q1 Score: 0/1.0 | Q2 Score: 0/1.5 | Q3 Score: 0/2.5 | Q4 Score: 0/2.0 | Q5 Score: 0/1.5 | Q6 Score: 0/1.5");
                    _logger.LogInformation("[Playwright] Total Score: 0/10.0");
                    _logger.LogInformation("[Playwright] ========================================");

                    stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Tests skipped: 0/6 passed (Build failed)", Level = "Error" });

                    result.Success = true;
                    result.AutoScore = 0;
                    result.TotalTests = 6;
                    result.PassedTests = 0;
                    result.Note = "Build failed";
                    result.TestResultDetail = CreateZeroScoreTestResultDetail("Build failed");
                    result.StepLogs = stepLogs;
                    result.Duration = DateTime.UtcNow - startTime;
                    return result;
                }
                _logger.LogInformation("[GradingPipeline] Build successful");
                stepLogs.Add(new GradingStepLog { Step = "Build", Message = "Build successful" });

                // Step 5.5: Update appsettings.json with connection string from appsettings.Development.json
                _logger.LogInformation("[GradingPipeline] Step 5.5: Updating appsettings.json connection string");
                stepLogs.Add(new GradingStepLog { Step = "Config", Message = "Updating appsettings.json connection string" });
                try
                {
                    await UpdateAppSettingsJsonAsync(projectPath, cancellationToken);
                    _logger.LogInformation("[GradingPipeline] appsettings.json updated successfully");
                    stepLogs.Add(new GradingStepLog { Step = "Config", Message = "appsettings.json updated successfully" });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GradingPipeline] Failed to update appsettings.json: {Message}", ex.Message);
                    stepLogs.Add(new GradingStepLog { Step = "Config", Message = $"Failed to update appsettings.json: {ex.Message}", Level = "Warning" });
                    // Continue anyway - app might work with existing config
                }

                // Step 6: Run Application
                _logger.LogInformation("[GradingPipeline] Step 6: Starting application...");
                stepLogs.Add(new GradingStepLog { Step = "Run", Message = "Starting application" });
                var app = await StartApplicationAsync(projectPath, submissionId, cancellationToken);
                if (app == null)
                {
                    _logger.LogError("[GradingPipeline] Application failed to start");
                    result.Success = false;
                    result.AutoScore = 0;
                    result.Note = "Application failed to start";
                    stepLogs.Add(new GradingStepLog { Step = "Run", Message = "Application failed to start", Level = "Error" });
                    return result;
                }
                // Track the application process
                _processTracker.TrackProcess(app.Process.Id, submissionId, $"Application for submission {submissionId}");
                _logger.LogInformation("[GradingPipeline] Application started successfully at {BaseUrl}", app.BaseUrl);
                stepLogs.Add(new GradingStepLog { Step = "Run", Message = "Application started successfully" });

                try
                {
                    // Step 7: Run UI Tests (Playwright)
                    _logger.LogInformation("[GradingPipeline] Step 7: Running UI tests with Playwright...");
                    stepLogs.Add(new GradingStepLog { Step = "Test", Message = "Running UI tests" });
                    TestResultDetail? testResultDetail = null;
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var uiTestService = scope.ServiceProvider.GetRequiredService<IUITestService>();
                        var (total, passed, detail) = await uiTestService.RunAsync(app.BaseUrl, cancellationToken);
                        testResultDetail = detail;
                        stepLogs.Add(new GradingStepLog { Step = "Test", Message = $"Tests completed: {passed}/{total} passed" });

                        result.Success = true;
                        result.AutoScore = total > 0 ? (passed * 100) / total : 0;
                        result.TotalTests = total;
                        result.PassedTests = passed;
                        result.Note = passed == total ? "All tests passed" : $"{passed}/{total} tests passed";
                    }

                    // Store testResultDetail in result for callback
                    result.StepLogs = stepLogs;
                    result.TestResultDetail = testResultDetail;

                    // scoring already set from ui test service above
                }
                finally
                {
                    // Step 8: Cleanup
                    stepLogs.Add(new GradingStepLog { Step = "Cleanup", Message = "Cleaning up temporary files" });
                    
                    // Kill application process
                    try
                    {
                        _processTracker.UntrackProcess(app.Process.Id);
                        if (!app.Process.HasExited)
                        {
                            app.Process.Kill();
                            await app.Process.WaitForExitAsync(cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        stepLogs.Add(new GradingStepLog { Step = "Cleanup", Message = $"Failed to kill process: {ex.Message}", Level = "Warning" });
                    }
                    
                    // Kill all processes for this submission and clean up files
                    await _processTracker.KillProcessesForSubmissionAsync(submissionId, 3000);
                    await CleanupAsync(submissionId, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.AutoScore = 0;
                result.Note = $"Processing failed: {ex.Message}";
                stepLogs.Add(new GradingStepLog { Step = "Error", Message = ex.Message, Level = "Error" });
            }
            finally
            {
                // Cleanup: Kill processes and remove temporary files
                await _processTracker.KillProcessesForSubmissionAsync(submissionId, 3000);
                await CleanupAsync(submissionId, cancellationToken);
                stepLogs.Add(new GradingStepLog { Step = "Cleanup", Message = "Cleanup completed" });
            }

            result.Duration = DateTime.UtcNow - startTime;
            result.StepLogs = stepLogs;
            return result;
        }

        private async Task<string> DownloadSubmissionAsync(string submissionId, string fileUrl, CancellationToken cancellationToken)
        {
            var zipPath = Path.Combine(_workingDirectory, $"{submissionId}.zip");
            return await _fileDownloadService.DownloadSubmissionZipAsync(fileUrl, zipPath, cancellationToken);
        }

        private async Task<string> ExtractZipAsync(string zipPath, CancellationToken cancellationToken)
        {
            var extractPath = Path.Combine(_workingDirectory, Path.GetFileNameWithoutExtension(zipPath));
            
            // Kill any orphaned processes that might be locking files from previous runs
            // Do this before checking if directory exists to catch processes from crashed runs
            _processTracker.KillAllProcessesInWorkingDirectory(_workingDirectory);
            await Task.Delay(2000, cancellationToken); // Give processes time to exit
            
            if (Directory.Exists(extractPath))
            {
                _processTracker.KillAllProcessesInWorkingDirectory(_workingDirectory);
                await Task.Delay(1000, cancellationToken); // Give processes more time to exit
            }
            
            // Delete existing directory if it exists, with retry logic for locked files
            if (Directory.Exists(extractPath))
            {
                DeleteDirectoryWithRetry(extractPath, maxRetries: 5, delayMs: 1000);
                
                // If still exists after retries, try to remove read-only attributes and delete again
                if (Directory.Exists(extractPath))
                {
                    try
                    {
                        // Force kill processes one more time before final delete attempt
                        _processTracker.KillAllProcessesInWorkingDirectory(_workingDirectory);
                        Thread.Sleep(2000); // Give more time for processes to exit
                        
                        RemoveReadOnlyAttributes(extractPath);
                        Thread.Sleep(500);
                        Directory.Delete(extractPath, true);
                    }
                    catch (Exception ex)
                    {
                        // If still can't delete, try to rename it instead to avoid conflicts
                        // But first, kill processes again before renaming
                        try
                        {
                            _processTracker.KillAllProcessesInWorkingDirectory(_workingDirectory);
                            Thread.Sleep(2000);
                        }
                        catch { /* ignore kill errors */ }
                        
                        var backupPath = extractPath + "_old_" + DateTime.Now.Ticks;
                        try
                        {
                            if (Directory.Exists(backupPath))
                                DeleteDirectoryWithRetry(backupPath, maxRetries: 2, delayMs: 500);
                            
                            // Try to remove read-only from files before rename
                            RemoveReadOnlyAttributes(extractPath);
                            Thread.Sleep(500);
                            
                            Directory.Move(extractPath, backupPath);
                            // Only log info if rename succeeded - this is a successful workaround
                            Console.WriteLine($"Info: Could not delete existing directory {extractPath}, renamed to {backupPath} instead");
                        }
                        catch (Exception renameEx)
                        {
                            // Only log warning if both delete and rename failed
                            Console.WriteLine($"Warning: Could not delete or rename existing directory {extractPath}: {ex.Message}. Rename attempt: {renameEx.Message}");
                            // Continue anyway - extraction might still work or overwrite some files
                            // The directory will remain but extraction should still work
                        }
                    }
                }
            }
            
            Directory.CreateDirectory(extractPath);
            
            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractPath), cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                // If access denied during extraction, try to remove read-only attributes and retry once
                try
                {
                    RemoveReadOnlyAttributes(extractPath);
                    Thread.Sleep(500);
                    // Delete conflicting files and extract again
                    DeleteDirectoryWithRetry(extractPath, maxRetries: 2, delayMs: 500);
                    Directory.CreateDirectory(extractPath);
                    await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractPath), cancellationToken);
                }
                catch
                {
                    // Re-throw original exception if retry also fails
                    throw new IOException($"Failed to extract ZIP file due to access denied. Path: {extractPath}. Original error: {ex.Message}", ex);
                }
            }
            catch (IOException ex) when (ex.Message.Contains("Access to the path") || ex.Message.Contains("is denied"))
            {
                // Similar handling for generic IOException with access denied
                try
                {
                    RemoveReadOnlyAttributes(extractPath);
                    Thread.Sleep(500);
                    // Delete conflicting files and extract again
                    DeleteDirectoryWithRetry(extractPath, maxRetries: 2, delayMs: 500);
                    Directory.CreateDirectory(extractPath);
                    await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractPath), cancellationToken);
                }
                catch
                {
                    throw new IOException($"Failed to extract ZIP file due to access denied. Path: {extractPath}. Original error: {ex.Message}", ex);
                }
            }
            
            // After initial extraction, check for nested archives and extract them recursively
            await ExtractNestedArchivesAsync(extractPath, cancellationToken);
            
            // After extracting all archives, unwrap nested folders with duplicate names
            // Example: extracted/ProjectName/ProjectName/ -> extracted/ProjectName/
            extractPath = UnwrapDuplicateFolders(extractPath);
            
            return extractPath;
        }

        private async Task ExtractNestedArchivesAsync(string directoryPath, CancellationToken cancellationToken, int maxDepth = 5, int currentDepth = 0)
        {
            if (currentDepth >= maxDepth)
            {
                _logger.LogWarning("[GradingPipeline] Maximum extraction depth ({MaxDepth}) reached in {Path}. Stopping recursive extraction.", maxDepth, directoryPath);
                return;
            }

            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            // Loop until no more archives are found (to ensure all nested archives are extracted)
            int iterationCount = 0;
            int maxIterations = 10; // Safety limit to prevent infinite loops
            
            // Track failed archives to avoid retrying them
            var failedArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            while (iterationCount < maxIterations)
            {
                iterationCount++;
                
                // Find ALL archive files recursively in the entire directory tree
                // This ensures we extract nested archives even if they are in subdirectories
                var archiveFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".zip" || ext == ".rar" || ext == ".7z";
                    })
                    .Where(f => !failedArchives.Contains(f)) // Skip failed archives
                    .OrderBy(f => f.Length) // Extract shorter paths first (top-level archives before nested ones)
                    .ToList();

                if (archiveFiles.Count == 0)
                {
                    // No more archives found, extraction complete
                    _logger.LogInformation("[GradingPipeline] No more archives found after {Iterations} iteration(s). Nested extraction complete.", iterationCount - 1);
                    break;
                }

                _logger.LogInformation("[GradingPipeline] Found {Count} archive(s) in iteration {Iteration}. Extracting...", archiveFiles.Count, iterationCount);

            foreach (var archiveFile in archiveFiles)
            {
                try
                {
                    var ext = Path.GetExtension(archiveFile).ToLowerInvariant();
                    var archiveName = Path.GetFileNameWithoutExtension(archiveFile);
                    var nestedExtractPath = Path.Combine(directoryPath, archiveName);

                    _logger.LogInformation("[GradingPipeline] Found nested archive: {ArchiveFile} at depth {Depth}. Extracting...", archiveFile, currentDepth + 1);

                    if (ext == ".zip")
                    {
                        // Extract ZIP file
                        if (Directory.Exists(nestedExtractPath))
                        {
                            DeleteDirectoryWithRetry(nestedExtractPath, maxRetries: 2, delayMs: 500);
                        }
                        Directory.CreateDirectory(nestedExtractPath);

                        await Task.Run(() => ZipFile.ExtractToDirectory(archiveFile, nestedExtractPath), cancellationToken);
                        
                        // Delete the extracted archive file
                        try
                        {
                            File.Delete(archiveFile);
                            _logger.LogInformation("[GradingPipeline] Deleted archive file: {ArchiveFile}", archiveFile);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[GradingPipeline] Failed to delete archive file {ArchiveFile}: {Message}", archiveFile, ex.Message);
                        }

                        // Note: No recursive call needed - the while loop will find and extract any new archives
                    }
                    else if (ext == ".rar")
                    {
                        // RAR extraction requires external tool (WinRAR, 7-Zip, etc.)
                        // For now, log a warning and try to use 7-Zip if available
                        _logger.LogWarning("[GradingPipeline] RAR file detected: {ArchiveFile}. RAR extraction requires external tool. Attempting with 7-Zip if available...", archiveFile);
                        
                        // Try to extract using 7-Zip command line tool (if installed)
                        var sevenZipPath = Find7ZipPath();
                        if (!string.IsNullOrEmpty(sevenZipPath))
                        {
                            try
                            {
                                if (Directory.Exists(nestedExtractPath))
                                {
                                    DeleteDirectoryWithRetry(nestedExtractPath, maxRetries: 2, delayMs: 500);
                                }
                                Directory.CreateDirectory(nestedExtractPath);

                                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = sevenZipPath,
                                    Arguments = $"x \"{archiveFile}\" -o\"{nestedExtractPath}\" -y",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                };

                                using var process = System.Diagnostics.Process.Start(processStartInfo);
                                if (process != null)
                                {
                                    await process.WaitForExitAsync(cancellationToken);
                                    if (process.ExitCode == 0)
                                    {
                                        _logger.LogInformation("[GradingPipeline] Successfully extracted RAR file: {ArchiveFile}", archiveFile);
                                        
                                        // Delete the extracted archive file
                                        try
                                        {
                                            File.Delete(archiveFile);
                                            _logger.LogInformation("[GradingPipeline] Deleted archive file: {ArchiveFile}", archiveFile);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning("[GradingPipeline] Failed to delete archive file {ArchiveFile}: {Message}", archiveFile, ex.Message);
                                        }

                                        // Note: No recursive call needed - the while loop will find and extract any new archives
                                    }
                                    else
                                    {
                                        _logger.LogWarning("[GradingPipeline] Failed to extract RAR file {ArchiveFile} with 7-Zip. Exit code: {ExitCode}", archiveFile, process.ExitCode);
                                        failedArchives.Add(archiveFile);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("[GradingPipeline] Error extracting RAR file {ArchiveFile}: {Message}", archiveFile, ex.Message);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[GradingPipeline] 7-Zip not found. Cannot extract RAR file: {ArchiveFile}", archiveFile);
                            failedArchives.Add(archiveFile);
                        }
                    }
                    else if (ext == ".7z")
                    {
                        // 7z extraction also requires external tool
                        _logger.LogWarning("[GradingPipeline] 7Z file detected: {ArchiveFile}. Attempting extraction with 7-Zip if available...", archiveFile);
                        
                        var sevenZipPath = Find7ZipPath();
                        if (!string.IsNullOrEmpty(sevenZipPath))
                        {
                            try
                            {
                                if (Directory.Exists(nestedExtractPath))
                                {
                                    DeleteDirectoryWithRetry(nestedExtractPath, maxRetries: 2, delayMs: 500);
                                }
                                Directory.CreateDirectory(nestedExtractPath);

                                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = sevenZipPath,
                                    Arguments = $"x \"{archiveFile}\" -o\"{nestedExtractPath}\" -y",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                };

                                using var process = System.Diagnostics.Process.Start(processStartInfo);
                                if (process != null)
                                {
                                    await process.WaitForExitAsync(cancellationToken);
                                    if (process.ExitCode == 0)
                                    {
                                        _logger.LogInformation("[GradingPipeline] Successfully extracted 7Z file: {ArchiveFile}", archiveFile);
                                        
                                        try
                                        {
                                            File.Delete(archiveFile);
                                            _logger.LogInformation("[GradingPipeline] Deleted archive file: {ArchiveFile}", archiveFile);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning("[GradingPipeline] Failed to delete archive file {ArchiveFile}: {Message}", archiveFile, ex.Message);
                                        }

                                        // Note: No recursive call needed - the while loop will find and extract any new archives
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("[GradingPipeline] Error extracting 7Z file {ArchiveFile}: {Message}", archiveFile, ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[GradingPipeline] Error processing nested archive {ArchiveFile}: {Message}", archiveFile, ex.Message);
                    // Mark as failed to avoid retrying
                    failedArchives.Add(archiveFile);
                    // Continue with other archives even if one fails
                }
            } // End foreach
            
            } // End while loop - continue to next iteration to find and extract any new archives
            
            if (iterationCount >= maxIterations)
            {
                _logger.LogWarning("[GradingPipeline] Maximum iterations ({MaxIterations}) reached. Some nested archives might not be extracted.", maxIterations);
            }
        }

        private string? Find7ZipPath()
        {
            // Check if running on Linux/Docker
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                // Linux paths for 7-Zip
                var linuxPaths = new[]
                {
                    "/usr/bin/7z",
                    "/usr/local/bin/7z",
                    "/usr/bin/7za",
                    "/usr/local/bin/7za"
                };

                foreach (var path in linuxPaths)
                {
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }

                // Try to find using 'which' command on Linux
                try
                {
                    var processStartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = "7z",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };

                    using var process = System.Diagnostics.Process.Start(processStartInfo);
                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit();
                        if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                        {
                            return output;
                        }
                    }
                }
                catch
                {
                    // Ignore errors
                }
            }
            else
            {
                // Windows paths
                var possiblePaths = new[]
                {
                    @"C:\Program Files\7-Zip\7z.exe",
                    @"C:\Program Files (x86)\7-Zip\7z.exe",
                    @"C:\ProgramData\chocolatey\bin\7z.exe"
                };

                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }

                // Try to find in PATH using 'where' command on Windows
                try
                {
                    var processStartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = "7z.exe",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };

                    using var process = System.Diagnostics.Process.Start(processStartInfo);
                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0 && File.Exists(lines[0]))
                        {
                            return lines[0];
                        }
                    }
                }
                catch
                {
                    // Ignore errors when searching PATH
                }
            }

            return null;
        }

        /// <summary>
        /// Unwraps nested folders with duplicate names recursively.
        /// Example: extracted/ProjectName/ProjectName/ -> extracted/ProjectName/
        /// Also dives into single subdirectories to find and unwrap nested duplicates.
        /// Repeats until no more duplicate nested folders are found.
        /// </summary>
        private string UnwrapDuplicateFolders(string extractPath)
        {
            try
            {
                _logger.LogInformation("[GradingPipeline] Checking for nested folders with duplicate names in: {Path}", extractPath);
                
                int unwrapCount = 0;
                int maxUnwrapIterations = 20; // Increased for deeper nesting
                string currentPath = extractPath;
                
                while (unwrapCount < maxUnwrapIterations)
                {
                    if (!Directory.Exists(currentPath))
                    {
                        _logger.LogWarning("[GradingPipeline] Directory does not exist: {Path}", currentPath);
                        break;
                    }
                    
                    // Get all subdirectories and files in current path
                    var subDirs = Directory.GetDirectories(currentPath);
                    var files = Directory.GetFiles(currentPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            // Exclude archive files from this check
                            return ext != ".zip" && ext != ".rar" && ext != ".7z";
                        })
                        .ToArray();
                    
                    // Check if there is exactly 1 subdirectory and no non-archive files at root level
                    if (subDirs.Length == 1 && files.Length == 0)
                    {
                        var singleSubDir = subDirs[0];
                        var currentDirName = Path.GetFileName(currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        var subDirName = Path.GetFileName(singleSubDir);
                        
                        // Check if the subdirectory has the same name as the current directory
                        if (string.Equals(currentDirName, subDirName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("[GradingPipeline] Found duplicate nested folder: {Current}/{Sub}", currentDirName, subDirName);
                            
                            // Move all contents from subdirectory to parent directory
                            var tempMovePath = currentPath + "_temp_unwrap_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            
                            try
                            {
                                // Step 1: Rename subdirectory to temp name to avoid conflicts
                                Directory.Move(singleSubDir, tempMovePath);
                                _logger.LogInformation("[GradingPipeline] Renamed {SubDir} to {TempPath}", singleSubDir, tempMovePath);
                                
                                // Step 2: Move all contents from temp directory to current directory
                                foreach (var subSubDir in Directory.GetDirectories(tempMovePath))
                                {
                                    var destDir = Path.Combine(currentPath, Path.GetFileName(subSubDir));
                                    if (Directory.Exists(destDir))
                                    {
                                        // If destination exists, merge contents recursively
                                        MergeDirectories(subSubDir, destDir);
                                        Directory.Delete(subSubDir, recursive: true);
                                    }
                                    else
                                    {
                                        Directory.Move(subSubDir, destDir);
                                    }
                                }
                                
                                foreach (var file in Directory.GetFiles(tempMovePath))
                                {
                                    var destFile = Path.Combine(currentPath, Path.GetFileName(file));
                                    File.Move(file, destFile, overwrite: true);
                                }
                                
                                // Step 3: Delete the now-empty temp directory
                                try
                                {
                                    Directory.Delete(tempMovePath, recursive: true);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning("[GradingPipeline] Failed to delete temp directory {TempPath}: {Message}", tempMovePath, ex.Message);
                                }
                                
                                _logger.LogInformation("[GradingPipeline] Successfully unwrapped duplicate nested folder: {DirName}", currentDirName);
                                unwrapCount++;
                                
                                // Continue to check if there are more duplicate nested folders at current level
                                continue;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("[GradingPipeline] Failed to unwrap duplicate nested folder {SubDir}: {Message}", singleSubDir, ex.Message);
                                
                                // Try to recover: rename temp back to original if it exists
                                try
                                {
                                    if (Directory.Exists(tempMovePath) && !Directory.Exists(singleSubDir))
                                    {
                                        Directory.Move(tempMovePath, singleSubDir);
                                    }
                                }
                                catch { }
                                
                                break;
                            }
                        }
                        else
                        {
                            // Subdirectory name doesn't match, but dive into it to check for duplicates inside
                            _logger.LogInformation("[GradingPipeline] Found single subdirectory with different name: {SubDirName}. Diving inside to check for duplicates...", subDirName);
                            currentPath = singleSubDir;
                            continue;
                        }
                    }
                    else
                    {
                        // Multiple subdirectories or files at root level
                        // But still need to check inside each subdirectory for duplicates
                        _logger.LogInformation("[GradingPipeline] Found {SubDirCount} subdirectories and {FileCount} files at current level.", subDirs.Length, files.Length);
                        
                        // Recursively check each subdirectory for duplicate nested folders
                        bool foundNestedDuplicate = false;
                        foreach (var subDir in subDirs)
                        {
                            var subDirUnwrapped = UnwrapDuplicateFoldersRecursive(subDir, ref unwrapCount, maxUnwrapIterations);
                            if (subDirUnwrapped != subDir)
                            {
                                foundNestedDuplicate = true;
                            }
                        }
                        
                        if (!foundNestedDuplicate)
                        {
                            // No more duplicates found anywhere
                            break;
                        }
                        
                        // After unwrapping subdirectories, check current level again
                        continue;
                    }
                }
                
                if (unwrapCount > 0)
                {
                    _logger.LogInformation("[GradingPipeline] Unwrapped {Count} duplicate nested folder(s) in total", unwrapCount);
                }
                else
                {
                    _logger.LogInformation("[GradingPipeline] No duplicate nested folders found");
                }
                
                return extractPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GradingPipeline] Error during unwrap duplicate folders: {Message}", ex.Message);
                return extractPath;
            }
        }

        /// <summary>
        /// Helper method to recursively unwrap duplicate folders in a subdirectory
        /// </summary>
        private string UnwrapDuplicateFoldersRecursive(string dirPath, ref int unwrapCount, int maxIterations)
        {
            try
            {
                if (!Directory.Exists(dirPath) || unwrapCount >= maxIterations)
                {
                    return dirPath;
                }

                var subDirs = Directory.GetDirectories(dirPath);
                var files = Directory.GetFiles(dirPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext != ".zip" && ext != ".rar" && ext != ".7z";
                    })
                    .ToArray();

                // Check if this directory has exactly 1 subdirectory with same name
                if (subDirs.Length == 1 && files.Length == 0)
                {
                    var singleSubDir = subDirs[0];
                    var currentDirName = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var subDirName = Path.GetFileName(singleSubDir);

                    if (string.Equals(currentDirName, subDirName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[GradingPipeline] Found duplicate nested folder in subdirectory: {DirPath}/{SubDir}", dirPath, subDirName);

                        var tempMovePath = dirPath + "_temp_unwrap_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                        try
                        {
                            Directory.Move(singleSubDir, tempMovePath);

                            foreach (var subSubDir in Directory.GetDirectories(tempMovePath))
                            {
                                var destDir = Path.Combine(dirPath, Path.GetFileName(subSubDir));
                                if (Directory.Exists(destDir))
                                {
                                    MergeDirectories(subSubDir, destDir);
                                    Directory.Delete(subSubDir, recursive: true);
                                }
                                else
                                {
                                    Directory.Move(subSubDir, destDir);
                                }
                            }

                            foreach (var file in Directory.GetFiles(tempMovePath))
                            {
                                var destFile = Path.Combine(dirPath, Path.GetFileName(file));
                                File.Move(file, destFile, overwrite: true);
                            }

                            try
                            {
                                Directory.Delete(tempMovePath, recursive: true);
                            }
                            catch { }

                            unwrapCount++;
                            _logger.LogInformation("[GradingPipeline] Successfully unwrapped duplicate in subdirectory: {DirName}", currentDirName);

                            // Recursively check again in case there are more layers
                            return UnwrapDuplicateFoldersRecursive(dirPath, ref unwrapCount, maxIterations);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[GradingPipeline] Failed to unwrap in subdirectory {DirPath}: {Message}", dirPath, ex.Message);
                            try
                            {
                                if (Directory.Exists(tempMovePath) && !Directory.Exists(singleSubDir))
                                {
                                    Directory.Move(tempMovePath, singleSubDir);
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // Dive deeper
                        return UnwrapDuplicateFoldersRecursive(singleSubDir, ref unwrapCount, maxIterations);
                    }
                }

                return dirPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GradingPipeline] Error in recursive unwrap for {DirPath}: {Message}", dirPath, ex.Message);
                return dirPath;
            }
        }

        /// <summary>
        /// Helper method to merge two directories recursively
        /// </summary>
        private void MergeDirectories(string sourceDir, string destDir)
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Move(file, destFile, overwrite: true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
                if (Directory.Exists(destSubDir))
                {
                    MergeDirectories(subDir, destSubDir);
                }
                else
                {
                    Directory.Move(subDir, destSubDir);
                }
            }
        }

        private async Task<Submission> ResolveSubmissionAsync(
            PRN232_Grading_System_GradingContext context,
            string submissionIdStr,
            string? examCode,
            string? studentId,
            string? examinerCode,
            CancellationToken cancellationToken)
        {
            // Parse submissionId - generate deterministic GUID from string if not a valid GUID format
            Guid submissionId;
            if (!Guid.TryParse(submissionIdStr, out submissionId))
            {
                // Generate deterministic GUID from string using MD5 hash
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(submissionIdStr));
                var guidBytes = new byte[16];
                Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);
                submissionId = new Guid(guidBytes);
            }
            
            var submission = new Submission
            {
                SubmissionId = submissionId
            };

            // Resolve ExamId from ExamCode
            if (!string.IsNullOrWhiteSpace(examCode))
            {
                var exam = await context.Exams.FirstOrDefaultAsync(e => e.Code == examCode, cancellationToken);
                if (exam != null)
                {
                    submission.ExamId = exam.ExamId;
                }
                else
                {
                    // Create new exam with the given code
                    submission.ExamId = Guid.NewGuid();
                    context.Exams.Add(new Exam
                    {
                        ExamId = submission.ExamId,
                        Code = examCode,
                        Title = $"Exam {examCode}",
                        CreatedAt = DateTime.UtcNow
                    });
                    await context.SaveChangesAsync(cancellationToken);
                }
            }
            else
            {
                submission.ExamId = Guid.Empty;
            }

            // Resolve StudentId
            // Store the original StudentId string from RabbitMQ message for later retrieval
            if (!string.IsNullOrWhiteSpace(studentId))
            {
                Guid parsedStudentId;
                if (!Guid.TryParse(studentId, out parsedStudentId))
                {
                    // Generate deterministic GUID from studentId string
                    using var md5 = MD5.Create();
                    var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(studentId));
                    var guidBytes = new byte[16];
                    Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);
                    parsedStudentId = new Guid(guidBytes);
                }
                
                // Store StudentId string for this submission (by SubmissionId)
                lock (_submissionStudentIdMap)
                {
                    _submissionStudentIdMap[submission.SubmissionId] = studentId;
                    // Also store mapping from StudentId GUID to StudentId string (for reverse lookup)
                    _studentIdGuidToStringMap[parsedStudentId] = studentId;
                }
                
                submission.StudentId = parsedStudentId;
            }
            else
            {
                submission.StudentId = Guid.Empty;
            }

            // Resolve ExaminerId from ExaminerCode
            if (!string.IsNullOrWhiteSpace(examinerCode))
            {
                var examiner = await context.Examiners.FirstOrDefaultAsync(e => e.Code == examinerCode, cancellationToken);
                if (examiner != null)
                {
                    submission.ExaminerId = examiner.ExaminerId;
                }
                else
                {
                    // Create new examiner with the given code
                    var newExaminerId = Guid.NewGuid();
                    submission.ExaminerId = newExaminerId;
                    context.Examiners.Add(new Examiner
                    {
                        ExaminerId = newExaminerId,
                        Code = examinerCode,
                        Name = $"Examiner {examinerCode}",
                        CreatedAt = DateTime.UtcNow
                    });
                    await context.SaveChangesAsync(cancellationToken);
                }
            }

            // Do NOT reuse by (ExamId, StudentId). We follow the incoming SubmissionId semantics:
            // - If SubmissionId exists in DB, we'll update that submission in PrepareSubmissionAsync
            // - If not, we'll create a new submission

            return submission;
        }

        private void RemoveBuildArtifacts(string rootPath)
        {
            try
            {
                var binObjDirs = new List<string>();
                
                // First, collect all bin/obj directories
                foreach (var dir in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
                    {
                        binObjDirs.Add(dir);
                    }
                }

                // Delete each directory with retry logic for locked files
                foreach (var dir in binObjDirs)
                {
                    DeleteDirectoryWithRetry(dir, maxRetries: 3, delayMs: 500);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - build artifacts removal is best-effort
                Console.WriteLine($"Warning: Failed to remove some build artifacts: {ex.Message}");
            }
        }

        private void DeleteDirectoryWithRetry(string path, int maxRetries = 3, int delayMs = 500)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path))
                        return;

                    // Remove read-only attributes from all files first
                    RemoveReadOnlyAttributes(path);

                    // Try to delete the directory
                    Directory.Delete(path, true);
                    return; // Success
                }
                catch (UnauthorizedAccessException)
                {
                    // File might be locked by a process - try to kill processes and unlock
                    try
                    {
                        RemoveReadOnlyAttributes(path);
                    }
                    catch { /* ignore */ }
                    
                    // If this is in working directory, kill orphaned processes
                    if (attempt == 1 && path.Contains(_workingDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        _processTracker.KillAllProcessesInWorkingDirectory(_workingDirectory);
                        Thread.Sleep(1000); // Give processes time to exit
                    }
                    
                    if (attempt < maxRetries - 1)
                    {
                        Thread.Sleep(delayMs);
                    }
                }
                catch (IOException ex) when (ex.Message.Contains("Access to the path") || ex.Message.Contains("is denied"))
                {
                    // File is locked by a process - kill processes and retry
                    if (attempt == 1 && path.Contains(_workingDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        _processTracker.KillAllProcessesInWorkingDirectory(_workingDirectory);
                        Thread.Sleep(1000); // Give processes time to exit
                    }
                    
                    // File might be in use, wait and retry
                    if (attempt < maxRetries - 1)
                    {
                        Thread.Sleep(delayMs);
                    }
                }
                catch (IOException)
                {
                    // Other IOException, wait and retry
                    if (attempt < maxRetries - 1)
                    {
                        Thread.Sleep(delayMs);
                    }
                }
                catch
                {
                    // Other errors, give up after retries
                    if (attempt == maxRetries - 1)
                        throw;
                }
            }
        }

        private void RemoveReadOnlyAttributes(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.IsReadOnly)
                    {
                        fileInfo.IsReadOnly = false;
                    }
                }
                else if (Directory.Exists(path))
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            if (fileInfo.IsReadOnly)
                            {
                                fileInfo.IsReadOnly = false;
                            }
                        }
                        catch { /* ignore individual file errors */ }
                    }
                }
            }
            catch { /* ignore */ }
        }

        private async Task<bool> BuildProjectAsync(string projectPath, CancellationToken cancellationToken)
        {
            // Prefer a Web project
            var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
            if (csprojFiles.Length == 0) return false;

            string? webCsproj = FindWebProject(csprojFiles);
            var csprojPath = webCsproj ?? csprojFiles[0];
            var projectDir = Path.GetDirectoryName(csprojPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                return false;

            var stdoutCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    stdoutCompletion.TrySetResult();
                }
                else
                {
                    _logger.LogInformation("[dotnet build][stdout] {Line}", args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    stderrCompletion.TrySetResult();
                }
                else
                {
                    _logger.LogWarning("[dotnet build][stderr] {Line}", args.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(stdoutCompletion.Task, stderrCompletion.Task);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignore kill failures
                }
                throw;
            }

            return process.ExitCode == 0;
        }

        private async Task<StaticAnalysisResult> RunStaticAnalysisAsync(string projectPath, CancellationToken cancellationToken)
        {
            var result = new StaticAnalysisResult();
            
            try
            {
                // Check 1: N-layer structure validation
                var nLayerValidation = await ValidateNLayerStructureAsync(projectPath);
                result.HasValidNLayerStructure = nLayerValidation.IsValid;
                
                // Check 2: Hardcoded connection strings
                result.HasHardcodedConnectionStrings = await CheckHardcodedConnectionStringsAsync(projectPath);
                
                // Check 3: Project structure compliance
                var projectStructureValidation = await ValidateProjectStructureAsync(projectPath);
                result.HasValidProjectStructure = projectStructureValidation.IsValid;
                
                // Check 4: Code quality checks
                result.HasCodeQualityIssues = await CheckCodeQualityAsync(projectPath);
                
                return result;
            }
            catch (Exception)
            {
                // If static analysis fails, assume issues exist (fail-safe)
                return new StaticAnalysisResult
                {
                    HasValidNLayerStructure = false,
                    HasHardcodedConnectionStrings = true,
                    HasValidProjectStructure = false,
                    HasCodeQualityIssues = true
                };
            }
        }

        private sealed class ValidationResult
        {
            public bool IsValid { get; set; }
            public string Details { get; set; } = string.Empty;
        }

        private async Task<ValidationResult> ValidateNLayerStructureAsync(string projectPath)
        {
            Console.WriteLine($"N-Layer Structure Check:");
            
            // Step 1: Find .sln file recursively
            var solutionFile = FindSolutionFile(projectPath);
            if (solutionFile == null)
            {
                Console.WriteLine($"  No .sln file found");
                Console.WriteLine($"  Result: FAIL");
                return new ValidationResult 
                { 
                    IsValid = false, 
                    Details = "No solution file (.sln) found in project" 
                };
            }
            
            var solutionDir = Path.GetDirectoryName(solutionFile);
            var solutionName = Path.GetFileName(solutionFile);
            Console.WriteLine($"  Solution file found: {solutionName}");
            Console.WriteLine($"  Solution directory: {Path.GetRelativePath(projectPath, solutionDir!)}");
            
            // Step 2: Check solution name format (must start with PE_PRN222_)
            if (!solutionName.StartsWith("PE_PRN222_", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  Solution name format invalid: {solutionName}");
                Console.WriteLine($"  Expected: starts with PE_PRN222_");
                Console.WriteLine($"  Result: FAIL");
                return new ValidationResult 
                { 
                    IsValid = false, 
                    Details = $"Solution name '{solutionName}' does not start with 'PE_PRN222_'" 
                };
            }
            Console.WriteLine($"  Solution name format: PASS");
            
            // Step 3: Check for >= 3 project folders in solution directory
            var projectFolders = Directory.GetDirectories(solutionDir!)
                .Where(d => Directory.GetFiles(d, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
                .ToList();
            
            Console.WriteLine($"  Project folders found: {projectFolders.Count}");
            foreach (var folder in projectFolders)
            {
                Console.WriteLine($"    - {Path.GetFileName(folder)}");
            }
            
            var isValid = projectFolders.Count >= 3;
            if (!isValid)
            {
                var projectNames = string.Join(", ", projectFolders.Select(f => Path.GetFileName(f)));
                Console.WriteLine($"  Result: FAIL (Need >= 3 projects, found {projectFolders.Count})");
                return new ValidationResult 
                { 
                    IsValid = false, 
                    Details = $"Project must have at least 3 projects, but found only {projectFolders.Count} project(s): {projectNames}" 
                };
            }
            
            Console.WriteLine($"  Result: PASS (Need >= 3 projects, found {projectFolders.Count})");
            return new ValidationResult { IsValid = true, Details = string.Empty };
        }

        private string? FindSolutionFile(string projectPath)
        {
            // Case 1: Check if .sln file exists directly in projectPath
            var directSolutionFiles = Directory.GetFiles(projectPath, "*.sln", SearchOption.TopDirectoryOnly);
            if (directSolutionFiles.Length > 0)
            {
                Console.WriteLine($"  Found .sln file directly in root: {Path.GetFileName(directSolutionFiles[0])}");
                return directSolutionFiles[0];
            }
            
            // Case 2: Look for .sln file in subdirectories (max depth 2)
            var allSolutionFiles = Directory.GetFiles(projectPath, "*.sln", SearchOption.AllDirectories);
            if (allSolutionFiles.Length > 0)
            {
                // Find the solution file that's closest to root (shortest path)
                var closestSolution = allSolutionFiles
                    .OrderBy(f => Path.GetRelativePath(projectPath, f).Split(Path.DirectorySeparatorChar).Length)
                    .First();
                
                Console.WriteLine($"  Found .sln file in subdirectory: {Path.GetRelativePath(projectPath, closestSolution)}");
                return closestSolution;
            }
            
            return null;
        }

        private async Task<bool> CheckHardcodedConnectionStringsAsync(string projectPath)
        {
            Console.WriteLine($"Hardcoded Connection Strings Check:");
            
            // Find solution file to get the correct directory
            var solutionFile = FindSolutionFile(projectPath);
            if (solutionFile == null)
            {
                Console.WriteLine($"  No .sln file found for connection string check");
                Console.WriteLine($"  Result: FAIL");
                return true;
            }
            
            var solutionDir = Path.GetDirectoryName(solutionFile);
            Console.WriteLine($"  Checking connection strings in: {Path.GetRelativePath(projectPath, solutionDir!)}");
            
            // Only check DbContext.cs files for hardcoded connection strings
            var dbContextFiles = Directory.GetFiles(solutionDir!, "*DbContext.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(solutionDir!, "DbContext.cs", SearchOption.AllDirectories));
            
            var violations = new List<string>();
            bool foundDbContext = false;
            
            var connectionTokens = new[]
            {
                "Server=",
                "Data Source=",
                "Initial Catalog=",
                "User ID=",
                "Password="
            };

            foreach (var file in dbContextFiles)
            {
                foundDbContext = true;
                var lines = await File.ReadAllLinesAsync(file);
                var relativePath = Path.GetRelativePath(solutionDir!, file);
                var inBlockComment = false;

                for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
                {
                    var code = StripCommentsFromLine(lines[lineNumber], ref inBlockComment);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    foreach (var token in connectionTokens)
                    {
                        if (code.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add($"{token} in {relativePath} at line {lineNumber + 1}");
                        }
                    }
                }
            }
            
            // Check if connection string exists in appsettings files
            var appsettingsFiles = Directory.GetFiles(solutionDir!, "appsettings*.json", SearchOption.AllDirectories);
            bool hasConnectionStringInConfig = false;
            
            foreach (var configFile in appsettingsFiles)
            {
                var configContent = await File.ReadAllTextAsync(configFile);
                if (configContent.Contains("ConnectionStrings", StringComparison.OrdinalIgnoreCase) &&
                    (configContent.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                     configContent.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)))
                {
                    hasConnectionStringInConfig = true;
                    Console.WriteLine($"  Found connection string in: {Path.GetRelativePath(solutionDir!, configFile)}");
                    break;
                }
            }
            
            if (violations.Count > 0)
            {
                Console.WriteLine($"  Violations found in DbContext: {string.Join(", ", violations)}");
                Console.WriteLine($"  Result: FAIL");
                return true;
            }
            
            if (!foundDbContext)
            {
                Console.WriteLine($"  No DbContext.cs files found to check");
            }
            
            if (!hasConnectionStringInConfig && appsettingsFiles.Length > 0)
            {
                Console.WriteLine($"  Warning: No connection string found in appsettings files");
            }
            
            Console.WriteLine($"  No hardcoded connection strings found in DbContext");
            Console.WriteLine($"  Result: PASS");
            return false;
        }

        private async Task<string> GetHardcodedConnectionStringDetailsAsync(string projectPath)
        {
            var solutionFile = FindSolutionFile(projectPath);
            if (solutionFile == null)
            {
                return string.Empty;
            }

            var solutionDir = Path.GetDirectoryName(solutionFile);
            var dbContextFiles = Directory.GetFiles(solutionDir!, "*DbContext.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(solutionDir!, "DbContext.cs", SearchOption.AllDirectories));

            var violations = new List<string>();
            var connectionTokens = new[]
            {
                "Server=",
                "Data Source=",
                "Initial Catalog=",
                "User ID=",
                "Password="
            };

            foreach (var file in dbContextFiles)
            {
                var lines = await File.ReadAllLinesAsync(file);
                var relativePath = Path.GetRelativePath(solutionDir!, file);
                var inBlockComment = false;

                for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
                {
                    var code = StripCommentsFromLine(lines[lineNumber], ref inBlockComment);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    foreach (var token in connectionTokens)
                    {
                        if (code.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add($"{token} in {relativePath} at line {lineNumber + 1}");
                        }
                    }
                }
            }

            return violations.Count > 0 ? string.Join("; ", violations) : string.Empty;
        }

        private async Task<ValidationResult> ValidateProjectStructureAsync(string projectPath)
        {
            Console.WriteLine($"Project Structure Check:");
            
            // Find solution file to get the correct directory
            var solutionFile = FindSolutionFile(projectPath);
            if (solutionFile == null)
            {
                Console.WriteLine($"  No .sln file found");
                Console.WriteLine($"  Result: FAIL");
                return new ValidationResult 
                { 
                    IsValid = false, 
                    Details = "No solution file (.sln) found" 
                };
            }
            
            var solutionDir = Path.GetDirectoryName(solutionFile);
            Console.WriteLine($"  Checking project structure in: {Path.GetRelativePath(projectPath, solutionDir!)}");
            
            // Check for required project files in solution directory
            var hasCsproj = Directory.GetFiles(solutionDir!, "*.csproj", SearchOption.AllDirectories).Length > 0;
            var hasAppsettings = Directory.GetFiles(solutionDir!, "appsettings*.json", SearchOption.AllDirectories).Length > 0;
            var hasProgramCs = Directory.GetFiles(solutionDir!, "Program.cs", SearchOption.AllDirectories).Length > 0;
            var hasSolution = Directory.GetFiles(solutionDir!, "*.sln", SearchOption.TopDirectoryOnly).Length > 0;
            
            Console.WriteLine($"  .csproj files: {hasCsproj}");
            Console.WriteLine($"  appsettings*.json files: {hasAppsettings}");
            Console.WriteLine($"  Program.cs files: {hasProgramCs}");
            Console.WriteLine($"  .sln files: {hasSolution}");
            
            var missingFiles = new List<string>();
            if (!hasCsproj) missingFiles.Add("*.csproj");
            if (!hasAppsettings) missingFiles.Add("appsettings*.json");
            if (!hasProgramCs) missingFiles.Add("Program.cs");
            if (!hasSolution) missingFiles.Add("*.sln");
            
            var isValid = hasCsproj && hasAppsettings && hasProgramCs && hasSolution;
            if (!isValid)
            {
                var details = $"Missing required files: {string.Join(", ", missingFiles)}";
                Console.WriteLine($"  Result: FAIL");
                return new ValidationResult { IsValid = false, Details = details };
            }
            
            Console.WriteLine($"  Result: PASS");
            return new ValidationResult { IsValid = true, Details = string.Empty };
        }

        private async Task<bool> CheckCodeQualityAsync(string projectPath)
        {
            Console.WriteLine($"Code Quality Check:");
            
            // Find solution file to get the correct directory
            var solutionFile = FindSolutionFile(projectPath);
            if (solutionFile == null)
            {
                Console.WriteLine($"  No .sln file found for code quality check");
                Console.WriteLine($"  Result: FAIL");
                return true;
            }
            
            var solutionDir = Path.GetDirectoryName(solutionFile);
            Console.WriteLine($"  Checking code quality in: {Path.GetRelativePath(projectPath, solutionDir!)}");
            
            // Check 1: Duplicate code detection (compare with previous submissions)
            var duplicateCheck = await CheckDuplicateCodeAsync(solutionDir!);
            if (duplicateCheck.HasDuplicates)
            {
                Console.WriteLine($"  Duplicate code detected: {duplicateCheck.DuplicateDetails}");
                Console.WriteLine($"  Result: FAIL");
                return true;
            }
            
            // Check 2: Forbidden keywords check
            var keywordCheck = await CheckRequiredKeywordsAsync(solutionDir!);
            if (!keywordCheck.HasRequiredKeywords)
            {
                Console.WriteLine($"  Forbidden keywords found: {keywordCheck.MissingKeywords}");
                Console.WriteLine($"  Result: FAIL");
                return true;
            }
            
            Console.WriteLine($"  No code quality issues found");
            Console.WriteLine($"  Result: PASS");
            return false;
        }

        private async Task<DuplicateCodeResult> CheckDuplicateCodeAsync(string projectPath)
        {
            Console.WriteLine($"    Duplicate Code Check:");
            
            // TODO: Implement duplicate code detection
            // For now, always return no duplicates (first submission)
            // In production, this would compare with previous submissions stored in database
            
            Console.WriteLine($"      No previous submissions to compare (first submission)");
            Console.WriteLine($"      Result: PASS");
            
            return new DuplicateCodeResult
            {
                HasDuplicates = false,
                DuplicateDetails = ""
            };
        }

        private async Task<KeywordCheckResult> CheckRequiredKeywordsAsync(string projectPath)
        {
            Console.WriteLine($"    Forbidden Keywords Check:");
            
            var forbiddenKeywords = (_validationSettings.ForbiddenKeywords?.Count > 0
                ? _validationSettings.ForbiddenKeywords
                : new List<string> { "Lion" }).ToArray();
            var codeFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);
            var foundKeywords = new HashSet<string>();
            var keywordDetails = new List<string>();
            
            // Flag to stop searching after first violation found (to avoid over 1000 characters in callback)
            bool foundFirstViolation = false;
            
            foreach (var file in codeFiles)
            {
                if (foundFirstViolation)
                    break;
                    
                var lines = await File.ReadAllLinesAsync(file);
                var relativePath = Path.GetRelativePath(projectPath, file);
                var inBlockComment = false;
                
                for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
                {
                    if (foundFirstViolation)
                        break;
                        
                    var code = StripCommentsFromLine(lines[lineNumber], ref inBlockComment);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }
                    
                    foreach (var keyword in forbiddenKeywords)
                    {
                        if (code.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            foundKeywords.Add(keyword);
                            // Only keep the first violation evidence to avoid over 1000 characters
                            keywordDetails.Add($"{keyword} at line {lineNumber + 1} in {relativePath}");
                            Console.WriteLine($"        {keyword} found in {relativePath} at line {lineNumber + 1}: {code.Trim()}");
                            foundFirstViolation = true;
                            break; // Stop after first violation
                        }
                    }
                }
            }
            
            if (foundKeywords.Count == 0)
            {
                Console.WriteLine($"      Found forbidden keywords: none");
                Console.WriteLine($"      Result: PASS");
            }
            else
            {
                Console.WriteLine($"      Found forbidden keywords: {string.Join(", ", foundKeywords)}");
                Console.WriteLine($"      Result: FAIL");
            }
            
            return new KeywordCheckResult
            {
                HasRequiredKeywords = foundKeywords.Count == 0, // PASS if no forbidden keywords found
                // Only return the first violation evidence (to avoid over 1000 characters in callback)
                MissingKeywords = foundKeywords.Count == 0 ? "" : $"Found violation keyword: {keywordDetails.FirstOrDefault() ?? string.Join(", ", foundKeywords)}",
                FoundKeywords = foundKeywords.ToList()
            };
        }

        private sealed class AppStartResult
        {
            public Process Process { get; set; } = default!;
            public string BaseUrl { get; set; } = string.Empty;
        }

        private async Task<AppStartResult?> StartApplicationAsync(string projectPath, string submissionId, CancellationToken cancellationToken)
        {
            // Find .csproj file and choose a Web project if possible
            var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
            if (csprojFiles.Length == 0) return null;

            string? webCsproj = FindWebProject(csprojFiles);
            var csprojPath = webCsproj ?? csprojFiles[0];
            var projectDir = Path.GetDirectoryName(csprojPath);

            // Find available port
            var port = FindAvailablePort();

            var baseUrl = $"http://localhost:{port}";
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --urls={baseUrl}",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Development" }
            };

            var process = Process.Start(startInfo);
            if (process == null)
                return null;

            _logger.LogInformation("[GradingPipeline] Application starting on port {Port}...", port);

            // Start reading output asynchronously
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    _logger.LogInformation("[dotnet run][stdout] {Line}", e.Data);
                }
            };
            
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                    _logger.LogWarning("[dotnet run][stderr] {Line}", e.Data);
                }
            };
            
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Health check with retries (tolerate any HTTP status)
            using var httpClient = new System.Net.Http.HttpClient();
            var started = false;
            for (var i = 0; i < 30 && !started; i++)
            {
                await Task.Delay(1000, cancellationToken);
                
                // Log progress every 5 seconds
                if (i % 5 == 0 && i > 0)
                {
                    _logger.LogInformation("[GradingPipeline] Still waiting for application to start... ({Attempt}/30)", i);
                }
                
                try
                {
                    var resp = await httpClient.GetAsync($"http://localhost:{port}", cancellationToken);
                    if (resp != null) started = true; // any response means listener is up
                }
                catch
                {
                    // Check if process has crashed
                    if (process.HasExited)
                    {
                        _logger.LogError("[GradingPipeline] Application process has exited with code {ExitCode}", process.ExitCode);
                        _logger.LogError("[GradingPipeline] stdout: {Output}", outputBuilder.ToString());
                        _logger.LogError("[GradingPipeline] stderr: {Error}", errorBuilder.ToString());
                        return null;
                    }
                    // ignore and retry
                }
            }

            if (!started)
            {
                _logger.LogError("[GradingPipeline] Application failed to start within 30 seconds");
                _logger.LogError("[GradingPipeline] stdout: {Output}", outputBuilder.ToString());
                _logger.LogError("[GradingPipeline] stderr: {Error}", errorBuilder.ToString());
                
                try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(cancellationToken); } } catch {}
                return null;
            }

            _logger.LogInformation("[GradingPipeline] Application started successfully on port {Port}", port);
            return new AppStartResult { Process = process, BaseUrl = baseUrl };
        }

        private async Task UpdateAppSettingsJsonAsync(string projectPath, CancellationToken cancellationToken)
        {
            // Read connection string from worker's appsettings.json via IConfiguration
            string targetConnectionString;
            try
            {
                targetConnectionString = _configuration.GetConnectionString("DefaultConnection") 
                    ?? throw new InvalidOperationException("DefaultConnection not found in configuration");
                
                if (string.IsNullOrWhiteSpace(targetConnectionString))
                {
                    throw new InvalidOperationException("DefaultConnection is empty in configuration");
                }
                
                _logger.LogInformation("[GradingPipeline] Loaded connection string from worker configuration: DefaultConnection");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GradingPipeline] Error reading DefaultConnection from configuration. Using fallback connection string.");
                targetConnectionString = "Server=localhost;User Id=sa;Pwd=12345;Database=SU25PantherDB;TrustServerCertificate=True;Encrypt=False";
            }

            // Find all appsettings.json files in the project
            var appsettingsFiles = Directory.GetFiles(projectPath, "appsettings*.json", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                .ToList();

            if (appsettingsFiles.Count == 0)
            {
                _logger.LogWarning("[GradingPipeline] No appsettings.json files found in project");
                return;
            }

            foreach (var appsettingsPath in appsettingsFiles)
            {
                try
                {
                    // Read appsettings.json
                    var settingsJson = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
                    var settings = JsonNode.Parse(settingsJson);

                    if (settings == null)
                    {
                        _logger.LogWarning("[GradingPipeline] Failed to parse appsettings.json at {Path}", appsettingsPath);
                        continue;
                    }

                    // Get ConnectionStrings section
                    if (settings["ConnectionStrings"] == null)
                    {
                        settings["ConnectionStrings"] = new JsonObject();
                    }

                    var connectionStrings = settings["ConnectionStrings"] as JsonObject;
                    if (connectionStrings == null)
                    {
                        _logger.LogWarning("[GradingPipeline] Failed to get ConnectionStrings section in {Path}", appsettingsPath);
                        continue;
                    }

                    bool hasChanges = false;

                    // Find and replace all connection strings that contain "Server=" or "Data Source="
                    foreach (var kvp in connectionStrings.ToList())
                    {
                        var key = kvp.Key;
                        var value = kvp.Value?.ToString() ?? string.Empty;
                        
                        // Remove quotes if present
                        var cleanValue = value.Trim('"', '\'');
                        
                        // Check if this connection string contains "Server=" or "Data Source="
                        if (cleanValue.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                            cleanValue.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
                        {
                            // Replace the value with target connection string, keep the key name
                            connectionStrings[key] = targetConnectionString;
                            hasChanges = true;
                            _logger.LogInformation("[GradingPipeline] Updated connection string '{Key}' in {Path}", key, appsettingsPath);
                        }
                    }

                    // If changes were made, write back the file
                    if (hasChanges)
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        var updatedJson = settings.ToJsonString(options);
                        await File.WriteAllTextAsync(appsettingsPath, updatedJson, cancellationToken);
                        _logger.LogInformation("[GradingPipeline] Updated appsettings.json at {Path}", appsettingsPath);
                    }
                    else
                    {
                        _logger.LogInformation("[GradingPipeline] No connection strings with 'Server=' or 'Data Source=' found in {Path}", appsettingsPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GradingPipeline] Error updating appsettings.json at {Path}", appsettingsPath);
                }
            }
        }

        private async Task UpdateConnectionStringReferenceAsync(string projectDir, List<string> oldConnectionStringNames, CancellationToken cancellationToken)
        {
            // Try to find and update Program.cs first
            var programCsFiles = Directory.GetFiles(projectDir, "Program.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                .ToList();

            foreach (var programCsPath in programCsFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(programCsPath);
                    var originalContent = content;
                    bool wasModified = false;

                    // Replace all old connection string names with "DefaultConnection"
                    foreach (var oldName in oldConnectionStringNames)
                    {
                        if (string.IsNullOrWhiteSpace(oldName))
                            continue;
                            
                        // Pattern to match GetConnectionString("oldName")
                        var pattern = $@"GetConnectionString\s*\(\s*""{Regex.Escape(oldName)}""\s*\)";
                        if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
                        {
                            content = Regex.Replace(
                                content,
                                pattern,
                                "GetConnectionString(\"DefaultConnection\")",
                                RegexOptions.IgnoreCase);
                            wasModified = true;
                            _logger.LogInformation("[GradingPipeline] Found and will replace '{OldName}' in Program.cs", oldName);
                        }
                        
                        // Also check for config["ConnectionStrings:oldName"] pattern
                        var configPattern = $@"config\s*\[\s*""ConnectionStrings:{Regex.Escape(oldName)}""\s*\]";
                        if (Regex.IsMatch(content, configPattern, RegexOptions.IgnoreCase))
                        {
                            content = Regex.Replace(
                                content,
                                configPattern,
                                "config[\"ConnectionStrings:DefaultConnection\"]",
                                RegexOptions.IgnoreCase);
                            wasModified = true;
                            _logger.LogInformation("[GradingPipeline] Found and will replace config['ConnectionStrings:{OldName}'] in Program.cs", oldName);
                        }
                    }
                    
                    // Check if it already uses "DefaultConnection" and no old names found
                    if (!wasModified && content.Contains("GetConnectionString(\"DefaultConnection\")", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[GradingPipeline] Program.cs already uses DefaultConnection at {Path}", programCsPath);
                        continue;
                    }
                    
                    if (wasModified && content != originalContent)
                    {
                        await File.WriteAllTextAsync(programCsPath, content);
                        _logger.LogInformation("[GradingPipeline] Updated Program.cs: Changed connection string names to DefaultConnection at {Path}", programCsPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GradingPipeline] Error updating Program.cs at {Path}", programCsPath);
                }
            }

            // Find solution directory to search entire solution
            // First, try to find solution file starting from projectDir
            var solutionFile = FindSolutionFile(projectDir);
            string? solutionDir = null;
            
            if (solutionFile != null)
            {
                solutionDir = Path.GetDirectoryName(solutionFile);
                _logger.LogInformation("[GradingPipeline] Found solution file at: {SolutionFile}, solution directory: {SolutionDir}", solutionFile, solutionDir);
            }
            else
            {
                // If not found, try to go up from projectDir to find .sln file
                var currentDir = projectDir;
                for (int i = 0; i < 3; i++) // Try up to 3 levels up
                {
                    var parentDir = Directory.GetParent(currentDir);
                    if (parentDir == null) break;
                    
                    var slnFiles = Directory.GetFiles(parentDir.FullName, "*.sln", SearchOption.TopDirectoryOnly);
                    if (slnFiles.Length > 0)
                    {
                        solutionFile = slnFiles[0];
                        solutionDir = parentDir.FullName;
                        _logger.LogInformation("[GradingPipeline] Found solution file by going up {Level} level(s): {SolutionFile}, solution directory: {SolutionDir}", i + 1, solutionFile, solutionDir);
                        break;
                    }
                    currentDir = parentDir.FullName;
                }
            }
            
            // Use solution directory if found, otherwise use projectDir
            var searchBaseDir = solutionDir ?? projectDir;
            
            _logger.LogInformation("[GradingPipeline] Will search for DBContext files in: {BaseDir}", searchBaseDir);
            
            // Find and update DBContext files - search for any file with "dbcontext" in name (case-insensitive)
            // Search for all .cs files in entire solution
            var allCsFiles = Directory.GetFiles(searchBaseDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                .ToList();
            
            _logger.LogInformation("[GradingPipeline] Found {Count} total .cs files in {BaseDir}", allCsFiles.Count, searchBaseDir);
            
            // Find all files with "dbcontext" in the filename (case-insensitive)
            var dbContextFiles = allCsFiles
                .Where(f =>
                {
                    var fileName = Path.GetFileName(f);
                    // Match files containing "dbcontext" anywhere in the name (case-insensitive)
                    var containsDbContext = fileName.IndexOf("dbcontext", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (containsDbContext)
                    {
                        _logger.LogInformation("[GradingPipeline] Found potential DBContext file: {File} (full path: {FullPath})", fileName, f);
                    }
                    return containsDbContext;
                })
                .Distinct()
                .ToList();
            
            _logger.LogInformation("[GradingPipeline] Searching in {BaseDir} for DBContext files", searchBaseDir);
            _logger.LogInformation("[GradingPipeline] Found {Count} DBContext file(s) to check: {Files}", 
                dbContextFiles.Count, string.Join(", ", dbContextFiles.Select(f => Path.GetFileName(f))));
            
            if (dbContextFiles.Count == 0)
            {
                _logger.LogWarning("[GradingPipeline] No DBContext files found! Searched in: {BaseDir}. All .cs files found: {AllFiles}", 
                    searchBaseDir, string.Join(", ", allCsFiles.Take(10).Select(f => Path.GetFileName(f))));
            }

            foreach (var dbContextPath in dbContextFiles)
            {
                try
                {
                    _logger.LogInformation("[GradingPipeline] Processing DBContext file: {Path}", dbContextPath);
                    var content = await File.ReadAllTextAsync(dbContextPath);
                    var originalContent = content;
                    bool wasModified = false;

                    // First, find all connection string names used in this file
                    var foundConnectionStringNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    
                    // Find all config["ConnectionStrings:..."] patterns
                    var configPattern = @"config\s*\[\s*([""'])\s*ConnectionStrings\s*:\s*([^""']+)\s*\1\s*\]";
                    var configMatches = Regex.Matches(content, configPattern, RegexOptions.IgnoreCase);
                    foreach (Match match in configMatches)
                    {
                        var connectionName = match.Groups[2].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(connectionName) && 
                            !connectionName.Equals("DefaultConnection", StringComparison.OrdinalIgnoreCase))
                        {
                            foundConnectionStringNames.Add(connectionName);
                            _logger.LogInformation("[GradingPipeline] Found connection string name '{Name}' in config[...] pattern in DBContext file", connectionName);
                        }
                    }
                    
                    // Find all GetConnectionString("...") patterns
                    var getConnectionPattern = @"GetConnectionString\s*\(\s*""\s*([^""]+)\s*""\s*\)";
                    var getConnectionMatches = Regex.Matches(content, getConnectionPattern, RegexOptions.IgnoreCase);
                    foreach (Match match in getConnectionMatches)
                    {
                        var connectionName = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(connectionName) && 
                            !connectionName.Equals("DefaultConnection", StringComparison.OrdinalIgnoreCase))
                        {
                            foundConnectionStringNames.Add(connectionName);
                            _logger.LogInformation("[GradingPipeline] Found connection string name '{Name}' in GetConnectionString(...) pattern in DBContext file", connectionName);
                        }
                    }
                    
                    // Combine with oldConnectionStringNames from appsettings.json
                    var allOldNames = new HashSet<string>(oldConnectionStringNames, StringComparer.OrdinalIgnoreCase);
                    foreach (var foundName in foundConnectionStringNames)
                    {
                        allOldNames.Add(foundName);
                    }
                    
                    _logger.LogInformation("[GradingPipeline] Will replace {Count} connection string name(s) in DBContext file: {Names}", 
                        allOldNames.Count, string.Join(", ", allOldNames));

                    // Replace all found connection string names with "DefaultConnection"
                    foreach (var oldName in allOldNames)
                    {
                        if (string.IsNullOrWhiteSpace(oldName))
                            continue;
                            
                        _logger.LogInformation("[GradingPipeline] Replacing connection string name '{OldName}' with 'DefaultConnection' in DBContext file", oldName);
                            
                        // Replace GetConnectionString("oldName") -> GetConnectionString("DefaultConnection")
                        // Use regex to handle case-insensitive and optional spaces
                        var getConnectionPattern2 = $@"GetConnectionString\s*\(\s*""\s*{Regex.Escape(oldName)}\s*""\s*\)";
                        if (Regex.IsMatch(content, getConnectionPattern2, RegexOptions.IgnoreCase))
                        {
                            content = Regex.Replace(
                                content,
                                getConnectionPattern2,
                                "GetConnectionString(\"DefaultConnection\")",
                                RegexOptions.IgnoreCase);
                            wasModified = true;
                            _logger.LogInformation("[GradingPipeline] Replaced GetConnectionString('{OldName}') with GetConnectionString('DefaultConnection')", oldName);
                        }
                        
                        // Replace config["ConnectionStrings:oldName"] -> config["ConnectionStrings:DefaultConnection"]
                        var configPattern1 = $@"config\s*\[\s*""\s*ConnectionStrings\s*:\s*{Regex.Escape(oldName)}\s*""\s*\]";
                        if (Regex.IsMatch(content, configPattern1, RegexOptions.IgnoreCase))
                        {
                            content = Regex.Replace(
                                content,
                                configPattern1,
                                "config[\"ConnectionStrings:DefaultConnection\"]",
                                RegexOptions.IgnoreCase);
                            wasModified = true;
                            _logger.LogInformation("[GradingPipeline] Replaced config[\"ConnectionStrings:{OldName}\"] with config[\"ConnectionStrings:DefaultConnection\"]", oldName);
                        }
                        
                        // Replace config['ConnectionStrings:oldName'] -> config['ConnectionStrings:DefaultConnection']
                        var configPattern2 = $@"config\s*\[\s*'\s*ConnectionStrings\s*:\s*{Regex.Escape(oldName)}\s*'\s*\]";
                        if (Regex.IsMatch(content, configPattern2, RegexOptions.IgnoreCase))
                        {
                            content = Regex.Replace(
                                content,
                                configPattern2,
                                "config['ConnectionStrings:DefaultConnection']",
                                RegexOptions.IgnoreCase);
                            wasModified = true;
                            _logger.LogInformation("[GradingPipeline] Replaced config['ConnectionStrings:{OldName}'] with config['ConnectionStrings:DefaultConnection']", oldName);
                        }
                    }
                    
                    // Final check: replace any remaining connection string names that are not "DefaultConnection"
                    // This catches any names we might have missed
                    var finalConfigPattern = @"config\s*\[\s*([""'])\s*ConnectionStrings\s*:\s*([^""']+)\s*\1\s*\]";
                    var finalConfigMatches = Regex.Matches(content, finalConfigPattern, RegexOptions.IgnoreCase);
                    foreach (Match match in finalConfigMatches)
                    {
                        var quoteChar = match.Groups[1].Value;
                        var connectionName = match.Groups[2].Value.Trim();
                        if (!connectionName.Equals("DefaultConnection", StringComparison.OrdinalIgnoreCase))
                        {
                            var newValue = $"config[{quoteChar}ConnectionStrings:DefaultConnection{quoteChar}]";
                            content = content.Replace(match.Value, newValue);
                            wasModified = true;
                            _logger.LogInformation("[GradingPipeline] Final check: Replaced config[{Quote}ConnectionStrings:{Name}{Quote}] with DefaultConnection", quoteChar, connectionName, quoteChar);
                        }
                    }
                    
                    var finalGetConnectionPattern = @"GetConnectionString\s*\(\s*""\s*([^""]+)\s*""\s*\)";
                    var finalGetConnectionMatches = Regex.Matches(content, finalGetConnectionPattern, RegexOptions.IgnoreCase);
                    foreach (Match match in finalGetConnectionMatches)
                    {
                        var connectionName = match.Groups[1].Value.Trim();
                        if (!connectionName.Equals("DefaultConnection", StringComparison.OrdinalIgnoreCase))
                        {
                            content = content.Replace(match.Value, "GetConnectionString(\"DefaultConnection\")");
                            wasModified = true;
                            _logger.LogInformation("[GradingPipeline] Final check: Replaced GetConnectionString('{Name}') with GetConnectionString('DefaultConnection')", connectionName);
                        }
                    }
                    
                    if (wasModified && content != originalContent)
                    {
                        await File.WriteAllTextAsync(dbContextPath, content);
                        _logger.LogInformation("[GradingPipeline] Updated DBContext file: Changed connection string names to DefaultConnection in {Path}", dbContextPath);
                    }
                    else if (!wasModified && content.Contains("DefaultConnection", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[GradingPipeline] DBContext file already uses DefaultConnection in {Path}", dbContextPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GradingPipeline] Error updating DBContext file at {Path}", dbContextPath);
                }
            }
        }

        private static string? FindWebProject(IEnumerable<string> csprojFiles)
        {
            // Heuristics: prefer SDK Web, or folder with .cshtml, or AspNetCore references
            foreach (var path in csprojFiles)
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains("Sdk=\"Microsoft.NET.Sdk.Web\"", StringComparison.OrdinalIgnoreCase))
                        return path;
                }
                catch {}
            }
            foreach (var path in csprojFiles)
            {
                var dir = Path.GetDirectoryName(path)!;
                var hasViews = Directory.GetFiles(dir, "*.cshtml", SearchOption.AllDirectories).Length > 0;
                if (hasViews) return path;
            }
            foreach (var path in csprojFiles)
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
                        return path;
                }
                catch {}
            }
            return null;
        }

        private async Task<string?> ReadBaseUrlFromProcessAsync(Process appProcess, CancellationToken cancellationToken)
        {
            try
            {
                // Try to parse from stdout first lines
                var output = await appProcess.StandardOutput.ReadLineAsync();
                // Fallback: not reliably available; callers provide the baseUrl
                return null;
            }
            catch { return null; }
        }

        private async Task CleanupAsync(string submissionId, CancellationToken cancellationToken)
        {
            try
            {
                var zipPath = Path.Combine(_workingDirectory, $"{submissionId}.zip");
                var extractPath = Path.Combine(_workingDirectory, submissionId);

                // Kill any processes using files in this directory before cleanup
                if (Directory.Exists(extractPath))
                {
                    _processTracker.KillAllProcessesInWorkingDirectory(_workingDirectory);
                    await Task.Delay(1000, cancellationToken); // Give processes time to exit
                }

                if (File.Exists(zipPath))
                {
                    try
                    {
                        File.Delete(zipPath);
                    }
                    catch
                    {
                        // Ignore zip file deletion errors
                    }
                }

                if (Directory.Exists(extractPath))
                {
                    try
                    {
                        DeleteDirectoryWithRetry(extractPath, maxRetries: 5, delayMs: 1000);
                    }
                    catch
                    {
                        // If still can't delete, try to rename it to avoid conflicts
                        try
                        {
                            var backupPath = extractPath + "_old_" + DateTime.Now.Ticks;
                            if (Directory.Exists(backupPath))
                                DeleteDirectoryWithRetry(backupPath, maxRetries: 2, delayMs: 500);
                            Directory.Move(extractPath, backupPath);
                            Console.WriteLine($"Info: Could not delete directory {extractPath}, renamed to {backupPath} instead");
                        }
                        catch
                        {
                            // Ignore rename errors - directory will be cleaned up later
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore cleanup errors
            }
        }

        private static int FindAvailablePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string StripCommentsFromLine(string line, ref bool inBlockComment)
        {
            var sb = new StringBuilder(line.Length);
            int i = 0;

            while (i < line.Length)
            {
                if (!inBlockComment && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    break;
                }

                if (!inBlockComment && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }

                if (inBlockComment)
                {
                    if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i += 2;
                        continue;
                    }

                    i++;
                    continue;
                }

                sb.Append(line[i]);
                i++;
            }

            return sb.ToString();
        }

        private sealed class TestResult { public int TotalTests { get; set; } public int PassedTests { get; set; } }
    }

    public sealed class StaticAnalysisResult
    {
        public bool HasValidNLayerStructure { get; set; }
        public bool HasHardcodedConnectionStrings { get; set; }
        public bool HasValidProjectStructure { get; set; }
        public bool HasCodeQualityIssues { get; set; }
    }

    public sealed class DuplicateCodeResult
    {
        public bool HasDuplicates { get; set; }
        public string DuplicateDetails { get; set; } = string.Empty;
    }

    public sealed class KeywordCheckResult
    {
        public bool HasRequiredKeywords { get; set; }
        public string MissingKeywords { get; set; } = string.Empty;
        public List<string> FoundKeywords { get; set; } = new();
    }
}

