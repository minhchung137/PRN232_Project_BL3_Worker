using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("submission")]
public partial class Submission
{
    [Key]
    [Column("submission_id")]
    public Guid SubmissionId { get; set; }

    [Column("exam_id")]
    public Guid ExamId { get; set; }

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("examiner_id")]
    public Guid? ExaminerId { get; set; }

    [Column("storage_key")]
    public string StorageKey { get; set; } = null!;

    [Column("status")]
    public string? Status { get; set; }

    [Column("total_files")]
    public int? TotalFiles { get; set; }

    [Column("total_lines")]
    public int? TotalLines { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("analyzed_at")]
    public DateTime? AnalyzedAt { get; set; }

    [InverseProperty("Submission")]
    public virtual ICollection<CodeEmbedding> CodeEmbeddings { get; set; } = new List<CodeEmbedding>();

    [InverseProperty("Submission")]
    public virtual ICollection<CodeFile> CodeFiles { get; set; } = new List<CodeFile>();

    [InverseProperty("Submission")]
    public virtual ICollection<CodeUnit> CodeUnits { get; set; } = new List<CodeUnit>();

    [InverseProperty("SubmissionId1Navigation")]
    public virtual ICollection<DuplicateDetection> DuplicateDetectionSubmissionId1Navigations { get; set; } = new List<DuplicateDetection>();

    [InverseProperty("SubmissionId2Navigation")]
    public virtual ICollection<DuplicateDetection> DuplicateDetectionSubmissionId2Navigations { get; set; } = new List<DuplicateDetection>();

    [ForeignKey("ExamId")]
    [InverseProperty("Submissions")]
    public virtual Exam Exam { get; set; } = null!;

    [ForeignKey("ExaminerId")]
    [InverseProperty("Submissions")]
    public virtual Examiner? Examiner { get; set; }
}
