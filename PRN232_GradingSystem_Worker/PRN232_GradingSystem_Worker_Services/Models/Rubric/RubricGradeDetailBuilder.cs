namespace PRN232_GradingSystem_Worker_Services.Models.Rubric;

public class RubricGradeDetailBuilder
{
    public static GradeDetailRequest BuildFromRubric(
        string submissionId,
        string examinerCode,
        ExamRubricDto rubric,
        TestResultDetail? detail,
        string? comment = null)
    {
        // submissionId string -> int (giữ logic bạn đang dùng)
        int submissionIdInt = 0;
        if (!int.TryParse(submissionId, out submissionIdInt))
            submissionIdInt = Math.Abs(submissionId.GetHashCode());

        var req = new GradeDetailRequest
        {
            SubmissionId = submissionIdInt,
            Marker = examinerCode,
            Comment = comment ?? string.Empty,
            GradeDetails = new List<GradeDetailItem>()
        };

        // Nếu rubric không có gì thì trả req rỗng (worker vẫn callback được)
        if (rubric?.Questions == null || rubric.Questions.Count == 0)
            return req;

        foreach (var q in rubric.Questions.OrderBy(x => x.Qcode))
        {
            if (q.Criteria == null) continue;

            foreach (var c in q.Criteria.OrderBy(x => x.Orderindex))
            {
                // Điểm và note: ưu tiên lấy từ TestResultDetail nếu bạn có map được theo sub_code
                // (tạm thời để 0, note rỗng — bạn sẽ map ở bước 4.2.6)
                req.GradeDetails.Add(new GradeDetailItem
                {
                    GradeId = 0,
                    QCode = q.Qcode ?? "",
                    SubCode = c.Content ?? "",
                    Point = 0,
                    Note = ""
                });
            }
        }

        return req;
    }
}