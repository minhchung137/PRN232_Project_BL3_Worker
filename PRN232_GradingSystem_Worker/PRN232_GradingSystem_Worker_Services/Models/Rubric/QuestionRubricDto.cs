namespace PRN232_GradingSystem_Worker_Services.Models.Rubric
{
    public class QuestionRubricDto
    {
        public int Questionid { get; set; }
        public string Qcode { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal Maxscore { get; set; }

        public List<CriteriaRubricDto> Criteria { get; set; } = new();
    }
}