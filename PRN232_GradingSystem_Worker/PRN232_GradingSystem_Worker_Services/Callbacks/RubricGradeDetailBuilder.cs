using Microsoft.Extensions.Logging;
using PRN232_GradingSystem_Worker_Services.Models;
using PRN232_GradingSystem_Worker_Services.Models.Rubric;

namespace PRN232_GradingSystem_Worker_Services.Callbacks;

public sealed class RubricGradeDetailBuilder
    {
        private readonly ILogger<RubricGradeDetailBuilder> _logger;

        public RubricGradeDetailBuilder(ILogger<RubricGradeDetailBuilder> logger)
        {
            _logger = logger;
        }

        public GradeDetailRequest Build(
            string submissionId,
            string examinerCode,
            TestResultDetail detail,
            ExamRubricDto rubric,
            string? comment = null)
        {
            int submissionIdInt;
            if (!int.TryParse(submissionId, out submissionIdInt))
                submissionIdInt = Math.Abs(submissionId.GetHashCode());

            var request = new GradeDetailRequest
            {
                SubmissionId = submissionIdInt,
                Marker = examinerCode,
                Comment = comment ?? string.Empty,
                GradeDetails = new List<GradeDetailItem>()
            };

            if (rubric?.Questions == null || rubric.Questions.Count == 0)
            {
                _logger.LogWarning(
                    "[RubricBuilder] Rubric has no questions. SubmissionId={SubmissionId}",
                    submissionId
                );
                return request;
            }

            foreach (var q in rubric.Questions)
            {
                // if (q.Criteria == null || q.Criteria.Count == 0)
                //     continue;
                //
                // foreach (var c in q.Criteria)
                // {
                //     var (point, note) = MapPoint(detail, c.Content);
                //
                //     request.GradeDetails.Add(new GradeDetailItem
                //     {
                //         GradeId = 0,
                //         QCode = q.Qcode,
                //         SubCode = c.Content,
                //         Point = point,
                //         Note = note
                //     });
                // }
            }

            _logger.LogInformation(
                "[RubricBuilder] Built GradeDetails | SubmissionId={SubmissionId} | Count={Count}",
                submissionId,
                request.GradeDetails.Count
            );

            return request;
        }

        private (decimal point, string? note) MapPoint(TestResultDetail d, string subCode)
        {
            var key = (subCode ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            return key switch
            {
                // // ===== Q1 =====
                // "login" => (d.Q1Login, d.Q1LoginNote),
                //
                // // ===== Q2 =====
                // "list all" => (d.Q2ListAll, d.Q2ListAllNote),
                // "list all 2" => (d.Q2ListAll2, d.Q2ListAll2Note),
                // "paging" => (d.Q2Pagging, d.Q2PaggingNote),
                //
                // // ===== Q3 =====
                // "add ok" => (d.Q3AddOk, d.Q3AddOkNote),
                // "display top" => (d.Q3DisplayTop, d.Q3DisplayTopNote),
                // "validation combobox" => (d.Q3ValidationCombobox, d.Q3ValidationComboboxNote),
                // "validation required" => (d.Q3ValidationRequired, d.Q3ValidationRequiredNote),
                // "validation - characters length" => (d.Q3ValidationLength, d.Q3ValidationLengthNote),
                // "validation - no special characters" => (d.Q3ValidationSpecialCharacters, d.Q3ValidationSpecialCharactersNote),
                //
                // // ===== Q4 =====
                // "update ok" => (d.Q4UpdateOk, d.Q4UpdateOkNote),
                // "update validation" => (d.Q4UpdateValidation, d.Q4UpdateValidationNote),
                //
                // // ===== Q5 =====
                // "test 1" => (d.Q5Test1, d.Q5Test1Note),
                // "test 2" => (d.Q5Test2, d.Q5Test2Note),
                // "test 3" => (d.Q5Test3, d.Q5Test3Note),
                //
                // // ===== Q6 =====
                // "delete with signalr" => (d.Q6DeleteWithSignalR, d.Q6DeleteWithSignalRNote),

                _ => (0m, $"No mapping for rubric criteria: {subCode}")
            };
        }
    }