using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PRN232_GradingSystem_Worker.Configuration;
using PRN232_GradingSystem_Worker_Repo.Models;
using PRN232_GradingSystem_Worker_Services.Implementations;
using PRN232_GradingSystem_Worker_Services.Interfaces;
using PRN232_GradingSystem_Worker_Services.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PRN232_GradingSystem_Worker.Hosted
{
    public sealed class GradingConsumerService : BackgroundService
    {
        private readonly ILogger<GradingConsumerService> _logger;
        private readonly RabbitMQConfiguration _rabbitMQConfig;
        private readonly IGradingPipeline _gradingPipeline;
        private readonly ICallbackService _callbackService;
        private readonly ProcessTrackerService _processTracker;
        private IConnection? _connection;
        private IModel? _channel;

        public GradingConsumerService(
            ILogger<GradingConsumerService> logger, 
            RabbitMQConfiguration rabbitMQConfig,
            IGradingPipeline gradingPipeline,
            ICallbackService callbackService,
            ProcessTrackerService processTracker)
        {
            _logger = logger;
            _rabbitMQConfig = rabbitMQConfig;
            _gradingPipeline = gradingPipeline;
            _callbackService = callbackService;
            _processTracker = processTracker;
        }

        //protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        //{
        //    while (!stoppingToken.IsCancellationRequested)
        //    {
        //        try
        //        {
        //            var factory = new ConnectionFactory
        //            {
        //                HostName = _rabbitMQConfig.Host,
        //                Port = _rabbitMQConfig.Port,
        //                UserName = _rabbitMQConfig.User,
        //                Password = _rabbitMQConfig.Password,
        //                VirtualHost = _rabbitMQConfig.VirtualHost,
        //                DispatchConsumersAsync = true,
        //                Ssl = { Enabled = _rabbitMQConfig.UseTls }
        //            };

        //            _logger.LogInformation("Attempting to connect to RabbitMQ at {Host}:{Port}", _rabbitMQConfig.Host, _rabbitMQConfig.Port);
        //            _connection = factory.CreateConnection();
        //            _logger.LogInformation("Connected to RabbitMQ successfully");

        //            _channel = _connection.CreateModel();
        //            _channel.BasicQos(0, (ushort)_rabbitMQConfig.Prefetch, false);

        //            _channel.QueueDeclare(_rabbitMQConfig.QueueName, durable: true, exclusive: false, autoDelete: false);

        //            var consumer = new AsyncEventingBasicConsumer(_channel);
        //            consumer.Received += async (ch, ea) =>
        //            {
        //                var body = ea.Body.ToArray();
        //                var message = Encoding.UTF8.GetString(body);
        //                try
        //                {
        //                    var job = JsonSerializer.Deserialize<GradingJob>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        //                    if (job == null || string.IsNullOrWhiteSpace(job.SubmissionId))
        //                    {
        //                        _logger.LogWarning("Invalid job payload: {Message}", message);
        //                        _channel!.BasicAck(ea.DeliveryTag, false);
        //                        return;
        //                    }

        //                    _logger.LogInformation("Processing submission {SubmissionId}", job.SubmissionId);

        //                    var result = await _gradingPipeline.ProcessSubmissionAsync(
        //                        job.SubmissionId,
        //                        job.FileUrl ?? string.Empty,
        //                        job.ExamCode,
        //                        job.StudentId,
        //                        job.ExaminerCode,
        //                        stoppingToken);

        //                    // Call Main Service callback API to update result with grade details
        //                    await SendCallbackAsync(job.SubmissionId, job.ExaminerCode ?? string.Empty, result, stoppingToken);

        //                    _channel!.BasicAck(ea.DeliveryTag, false);
        //                }
        //                catch (Exception ex)
        //                {
        //                    _logger.LogError(ex, "Error processing job: {Payload}", message);
        //                    _channel!.BasicNack(ea.DeliveryTag, false, requeue: false);
        //                }
        //            };

        //            _channel.BasicConsume(queue: _rabbitMQConfig.QueueName, autoAck: false, consumer: consumer);
        //            _logger.LogInformation("Started consuming messages from queue: {Queue}", _rabbitMQConfig.QueueName);

        //            // Keep the connection alive until cancellation or error
        //            await Task.Delay(Timeout.Infinite, stoppingToken);
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "RabbitMQ connection error. Will retry in 30 seconds...");
        //            CleanupConnection();
        //            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        //        }
        //    }
        //}

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // --- SỬA ĐOẠN NÀY ---
                    // Ghi cứng thông tin Admin bạn vừa tạo để đảm bảo kết nối thành công
                    var factory = new ConnectionFactory
                    {
                        HostName = "localhost",
                        Port = 5672,
                        UserName = "admin",      // User bạn đã tạo trên Web
                        Password = "admin123",   // Pass bạn đã tạo trên Web
                        VirtualHost = "/",       // Quan trọng: Dấu gạch chéo
                        DispatchConsumersAsync = true
                    };
                    // --------------------

                    _logger.LogInformation("Attempting to connect to RabbitMQ at {Host}:{Port}", factory.HostName, factory.Port);
                    _connection = factory.CreateConnection();
                    _logger.LogInformation("Connected to RabbitMQ successfully");

                    _channel = _connection.CreateModel();

                    // Lưu ý: Ép kiểu int sang ushort cho PrefetchCount
                    _channel.BasicQos(0, (ushort)1, false);

                    // Khai báo hàng đợi (đảm bảo tên queue đúng)
                    var queueName = "grading.upload";
                    _channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false);

                    var consumer = new AsyncEventingBasicConsumer(_channel);
                    consumer.Received += async (ch, ea) =>
                    {
                        var body = ea.Body.ToArray();
                        var message = Encoding.UTF8.GetString(body);
                        try
                        {
                            // ... (Giữ nguyên logic xử lý tin nhắn bên dưới) ...
                            var job = JsonSerializer.Deserialize<GradingJob>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                            if (job == null || string.IsNullOrWhiteSpace(job.SubmissionId))
                            {
                                _logger.LogWarning("Invalid job payload: {Message}", message);
                                _channel!.BasicAck(ea.DeliveryTag, false);
                                return;
                            }

                            _logger.LogInformation("Processing submission {SubmissionId}", job.SubmissionId);

                            var result = await _gradingPipeline.ProcessSubmissionAsync(
                                job.SubmissionId,
                                job.FileUrl ?? string.Empty,
                                job.ExamCode,
                                job.StudentId,
                                job.ExaminerCode,
                                job.EntityName,
                                stoppingToken);

                            await SendCallbackAsync(job.SubmissionId, job.ExaminerCode ?? string.Empty, result, stoppingToken);

                            _channel!.BasicAck(ea.DeliveryTag, false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing job: {Payload}", message);
                            _channel!.BasicNack(ea.DeliveryTag, false, requeue: false);
                        }
                    };

                    _channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
                    _logger.LogInformation("Started consuming messages from queue: {Queue}", queueName);

                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RabbitMQ connection error. Will retry in 30 seconds...");
                    CleanupConnection();
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }

        private async Task SendCallbackAsync(string submissionId,string examinerCode,GradingResult result,CancellationToken cancellationToken)
        {
            try
            {
                GradeDetailRequest request;

                // Ưu tiên: Nếu có ApiGradingResult (tiêu chí mới), dùng nó trực tiếp
                if (result.ApiGradingResult != null)
                {
                    _logger.LogInformation(
                        "Submission {SubmissionId} has ApiGradingResult. Building GradeDetailRequest from API grading criteria.",
                        submissionId);

                    // Set comment only for validation failures
                    string comment = string.Empty;
                    if (!string.IsNullOrWhiteSpace(result.Note))
                    {
                        var noteLower = result.Note.ToLowerInvariant();
                        if (noteLower.Contains("violation") ||
                            noteLower.Contains("no solution file") ||
                            noteLower.Contains("build failed") ||
                            noteLower.Contains("application failed to start") ||
                            noteLower.Contains("duplicate code") ||
                            noteLower.Contains("processing failed"))
                        {
                            comment = result.Note;
                        }
                    }

                    request = BuildGradeDetailRequestFromApiGrading(
                        submissionId,
                        examinerCode,
                        result.ApiGradingResult,
                        comment
                    );
                }
                // Fallback: Nếu có TestResultDetail (code cũ - Playwright), dùng nó
                else if (result.TestResultDetail != null)
                {
                    _logger.LogInformation(
                        "Submission {SubmissionId} has TestResultDetail. Building GradeDetailRequest from TestResultDetail.",
                        submissionId);

                    // Set comment only for validation failures
                    string comment = string.Empty;
                    if (!string.IsNullOrWhiteSpace(result.Note))
                    {
                        var noteLower = result.Note.ToLowerInvariant();
                        if (noteLower.Contains("violation") ||
                            noteLower.Contains("no solution file") ||
                            noteLower.Contains("build failed") ||
                            noteLower.Contains("application failed to start") ||
                            noteLower.Contains("duplicate code") ||
                            noteLower.Contains("processing failed"))
                        {
                            comment = result.Note;
                        }
                    }

                    request = BuildGradeDetailRequest(
                        submissionId,
                        examinerCode,
                        result.TestResultDetail,
                        comment
                    );
                }
                else
                {
                    _logger.LogWarning(
                        "Submission {SubmissionId} has no TestResultDetail or ApiGradingResult. Skip callback.",
                        submissionId);
                    return;
                }

                await _callbackService.SendGradeDetailAsync(request, cancellationToken);

                _logger.LogInformation(
                    "Successfully sent grade detail callback for submission {SubmissionId}",
                    submissionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to send grade detail callback for submission {SubmissionId}",
                    submissionId);
                // Không throw – grading đã xong
            }
        }


        private GradeDetailRequest BuildGradeDetailRequest(string submissionId, string examinerCode, TestResultDetail detail, string? comment = null)
        {
            // Parse submissionId to int (if it's a GUID string, we'll use 0 as fallback)
            int submissionIdInt = 0;
            if (!int.TryParse(submissionId, out submissionIdInt))
            {
                // If submissionId is not a number (e.g., GUID), use 0 or hash to int
                submissionIdInt = Math.Abs(submissionId.GetHashCode());
            }

            var request = new GradeDetailRequest
            {
                SubmissionId = submissionIdInt,
                Marker = examinerCode,
                Comment = comment ?? string.Empty, // Set comment from GradingResult.Note (for validation failures)
                GradeDetails = new List<GradeDetailItem>()
            };

            // Q1: Login
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q1",
                SubCode = "Login",
                Point = detail.Q1Login,
                Note = detail.Q1LoginNote
            });

            // Q2: List All, List All 2, Paging
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q2",
                SubCode = "List All",
                Point = detail.Q2ListAll,
                Note = detail.Q2ListAllNote
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q2",
                SubCode = "List All 2",
                Point = detail.Q2ListAll2,
                Note = detail.Q2ListAll2Note
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q2",
                SubCode = "Paging",
                Point = detail.Q2Pagging,
                Note = detail.Q2PaggingNote
            });

            // Q3: Add OK, Display Top, Validation Combobox, Validation Required, Validation - Characters Length, Validation - No Special Characters
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q3",
                SubCode = "Add OK",
                Point = detail.Q3AddOk,
                Note = detail.Q3AddOkNote
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q3",
                SubCode = "Display Top",
                Point = detail.Q3DisplayTop,
                Note = detail.Q3DisplayTopNote
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q3",
                SubCode = "Validation Combobox",
                Point = detail.Q3ValidationCombobox,
                Note = detail.Q3ValidationComboboxNote
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q3",
                SubCode = "Validation Required",
                Point = detail.Q3ValidationRequired,
                Note = detail.Q3ValidationRequiredNote
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q3",
                SubCode = "Validation - Characters Length",
                Point = detail.Q3ValidationLength,
                Note = detail.Q3ValidationLengthNote
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q3",
                SubCode = "Validation - No Special Characters",
                Point = detail.Q3ValidationSpecialCharacters,
                Note = detail.Q3ValidationSpecialCharactersNote
            });

            // Q4: Update OK, Update Validation
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q4",
                SubCode = "Update OK",
                Point = detail.Q4UpdateOk,
                Note = detail.Q4UpdateOkNote
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q4",
                SubCode = "Update Validation",
                Point = detail.Q4UpdateValidation,
                Note = detail.Q4UpdateValidationNote
            });

            // Q5: Test 1, Test 2, Test 3
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q5",
                SubCode = "Test 1",
                Point = detail.Q5Test1,
                Note = detail.Q5Test1Note
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q5",
                SubCode = "Test 2",
                Point = detail.Q5Test2,
                Note = detail.Q5Test2Note
            });
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q5",
                SubCode = "Test 3",
                Point = detail.Q5Test3,
                Note = detail.Q5Test3Note
            });

            // Q6: Delete with SignalR
            request.GradeDetails.Add(new GradeDetailItem
            {
                GradeId = 0,
                QCode = "Q6",
                SubCode = "Delete with SignalR",
                Point = detail.Q6DeleteWithSignalR,
                Note = detail.Q6DeleteWithSignalRNote
            });

            return request;
        }

        /// <summary>
        /// Build GradeDetailRequest trực tiếp từ ApiGradingResultResponse (tiêu chí mới - API grading)
        /// Mỗi endpoint (Create, Update, Delete, GetAll, GetById, Search) trở thành 1 GradeDetailItem
        /// </summary>
        private GradeDetailRequest BuildGradeDetailRequestFromApiGrading(
            string submissionId, 
            string examinerCode, 
            ApiGradingResultResponse apiGradingResult, 
            string? comment = null)
        {
            // Parse submissionId to int
            int submissionIdInt = 0;
            if (!int.TryParse(submissionId, out submissionIdInt))
            {
                submissionIdInt = Math.Abs(submissionId.GetHashCode());
            }

            var request = new GradeDetailRequest
            {
                SubmissionId = submissionIdInt,
                Marker = examinerCode,
                Comment = comment ?? string.Empty,
                GradeDetails = new List<GradeDetailItem>()
            };

            // Null check: Nếu apiGradingResult hoặc EndpointScores null/empty, trả về request rỗng
            if (apiGradingResult == null || apiGradingResult.EndpointScores == null || apiGradingResult.EndpointScores.Count == 0)
            {
                _logger.LogWarning(
                    "ApiGradingResult or EndpointScores is null/empty. Returning empty GradeDetailRequest.");
                return request;
            }

            // Helper: Build note từ CriteriaChecks
            string BuildNoteFromCriteria(EndpointScoreResponse endpointScore)
            {
                if (endpointScore == null)
                {
                    return "Endpoint score is null.";
                }

                if (!endpointScore.EndpointExists)
                {
                    return $"Endpoint không tồn tại. Kỳ vọng: {endpointScore.Endpoint ?? "N/A"}";
                }

                if (endpointScore.CriteriaChecks == null || endpointScore.CriteriaChecks.Count == 0)
                {
                    return "Endpoint tồn tại nhưng không có tiêu chí chi tiết.";
                }

                var parts = new List<string>();
                foreach (var criteria in endpointScore.CriteriaChecks)
                {
                    if (criteria == null) continue;

                    if (criteria.Passed)
                    {
                        parts.Add($"✓ {criteria.Description ?? "N/A"}");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(criteria.Details))
                        {
                            parts.Add($"✗ {criteria.Description ?? "N/A"} ({criteria.Details})");
                        }
                        else
                        {
                            parts.Add($"✗ {criteria.Description ?? "N/A"}");
                        }
                    }
                }

                return parts.Count > 0 ? string.Join(" | ", parts) : "Không có tiêu chí nào được kiểm tra.";
            }

            // Map từng endpoint trong ApiGradingResultResponse → GradeDetailItem
            // Sử dụng QCode = "API" để phân biệt với code cũ (Q1..Q6)
            // SubCode = tên endpoint (Create, Update, Delete, GetAll, GetById, Search)
            foreach (var endpointScore in apiGradingResult.EndpointScores)
            {
                // Lấy tên endpoint ngắn gọn từ Function
                // Lưu ý: Function name từ ApiGradingService là "Get by ID (Chi tiết)" (theo tiêu chí hardcode)
                // Cần check "Get by ID" TRƯỚC "Get All" để tránh match sai
                string GetEndpointSubCode(string function)
                {
                    // Check Create, Update, Delete trước
                    if (function.Contains("Create", StringComparison.OrdinalIgnoreCase)) return "Create";
                    if (function.Contains("Update", StringComparison.OrdinalIgnoreCase)) return "Update";
                    if (function.Contains("Delete", StringComparison.OrdinalIgnoreCase)) return "Delete";
                    
                    // Check "Get by ID" TRƯỚC "Get All" để tránh match sai
                    // Pattern: "Get by ID" hoặc "GetById" (có thể có thêm text như "(Chi tiết)")
                    if (function.Contains("Get by ID", StringComparison.OrdinalIgnoreCase) || 
                        function.Contains("GetById", StringComparison.OrdinalIgnoreCase)) 
                        return "GetById";
                    
                    // Check "Get All" sau "Get by ID"
                    if (function.Contains("Get All", StringComparison.OrdinalIgnoreCase) || 
                        function.Contains("GetAll", StringComparison.OrdinalIgnoreCase)) 
                        return "GetAll";
                    
                    // Check Search
                    if (function.Contains("Search", StringComparison.OrdinalIgnoreCase)) return "Search";
                    
                    // Fallback: lấy từ đầu
                    return function.Split(' ')[0];
                }

                // Null check cho endpointScore
                if (endpointScore == null)
                {
                    _logger.LogWarning("Skipping null endpointScore in ApiGradingResult.");
                    continue;
                }

                var subCode = GetEndpointSubCode(endpointScore.Function ?? string.Empty);
                var note = BuildNoteFromCriteria(endpointScore);

                request.GradeDetails.Add(new GradeDetailItem
                {
                    GradeId = 0, // Sẽ được API set khi tạo Grade
                    QCode = "API", // Dùng "API" để phân biệt với Q1..Q6 cũ
                    SubCode = subCode, // Tên endpoint: Create, Update, Delete, GetAll, GetById, Search
                    Point = endpointScore.Score,
                    Note = note
                });
            }

            // Log warning nếu không có grade details nào
            if (request.GradeDetails.Count == 0)
            {
                _logger.LogWarning(
                    "No grade details were created from ApiGradingResult. EndpointScores may be empty or invalid.");
            }
            else
            {
                _logger.LogInformation(
                    "Created {Count} grade details from ApiGradingResult.", request.GradeDetails.Count);
            }

            return request;
        }

        private void CleanupConnection()
        {
            try
            {
                _channel?.Close();
                _channel?.Dispose();
                _connection?.Close();
                _connection?.Dispose();
                _logger.LogInformation("Cleaned up RabbitMQ connections");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up RabbitMQ connections");
            }
            finally
            {
                _channel = null;
                _connection = null;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping GradingConsumerService...");
            
            // Kill all tracked processes before shutdown
            try
            {
                await _processTracker.KillAllTrackedProcessesAsync(5000);
                _logger.LogInformation("All tracked processes have been stopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping tracked processes");
            }
            
            // Also kill all processes in working directory as a safety measure
            try
            {
                var workingDir = Path.Combine(Path.GetTempPath(), "GradingWorker");
                _processTracker.KillAllProcessesInWorkingDirectory(workingDir);
                _logger.LogInformation("All processes in working directory have been stopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping processes in working directory");
            }
            
            CleanupConnection();
            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            CleanupConnection();
            base.Dispose();
        }

        private sealed class GradingJob
        {
            public string SubmissionId { get; set; } = string.Empty;
            public string? FileUrl { get; set; }
            public string? ExamCode { get; set; }
            public string? StudentId { get; set; }
            public string? ExaminerCode { get; set; }
            public string? EntityName { get; set; } // Entity name for API grading (e.g., "Student", "Product")
        }
    }
}


