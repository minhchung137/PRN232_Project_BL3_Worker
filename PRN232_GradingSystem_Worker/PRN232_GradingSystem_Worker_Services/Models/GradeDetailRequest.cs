using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PRN232_GradingSystem_Worker_Services.Models
{
    public class GradeDetailRequest
    {
        [JsonPropertyName("submissionid")]
        public int SubmissionId { get; set; }
        
        [JsonPropertyName("marker")]
        public string Marker { get; set; } = string.Empty;
        
        [JsonPropertyName("comment")]
        public string Comment { get; set; } = string.Empty;
        
        [JsonPropertyName("gradedetails")]
        public List<GradeDetailItem> GradeDetails { get; set; } = new();
    }

    public class GradeDetailItem
    {
        [JsonPropertyName("gradeid")]
        public int GradeId { get; set; } = 0;
        
        [JsonPropertyName("qcode")]
        public string QCode { get; set; } = string.Empty;
        
        [JsonPropertyName("subcode")]
        public string SubCode { get; set; } = string.Empty;
        
        [JsonPropertyName("point")]
        public double Point { get; set; }
        
        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;
    }
}
