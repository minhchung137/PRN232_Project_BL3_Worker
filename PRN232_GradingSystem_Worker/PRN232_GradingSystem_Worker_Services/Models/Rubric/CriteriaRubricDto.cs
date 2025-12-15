namespace PRN232_GradingSystem_Worker_Services.Models.Rubric
{
    public class CriteriaRubricDto
    {
        public int Criteriaid { get; set; }
        public int Orderindex { get; set; }
        public string Content { get; set; } = string.Empty;
        public decimal Weight { get; set; }
        public bool Ismanual { get; set; }
    }
}