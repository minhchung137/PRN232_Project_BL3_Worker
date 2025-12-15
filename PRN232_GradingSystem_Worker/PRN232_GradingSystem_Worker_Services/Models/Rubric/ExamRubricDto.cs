namespace PRN232_GradingSystem_Worker_Services.Models.Rubric
{
    public class ExamRubricDto
    {
        public string Examcode { get; set; } = string.Empty;
        public List<QuestionRubricDto> Questions { get; set; } = new();
    }
}