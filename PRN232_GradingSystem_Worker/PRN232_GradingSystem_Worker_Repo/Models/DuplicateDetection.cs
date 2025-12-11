using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("duplicate_detection")]
public partial class DuplicateDetection
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("exam_id")]
    public Guid ExamId { get; set; }

    [Column("examiner_id")]
    public Guid? ExaminerId { get; set; }

    [Column("submission_id1")]
    public Guid SubmissionId1 { get; set; }

    [Column("submission_id2")]
    public Guid SubmissionId2 { get; set; }

    [Column("student_id1")]
    public Guid StudentId1 { get; set; }

    [Column("student_id2")]
    public Guid StudentId2 { get; set; }

    [Column("vector_score")]
    [Precision(6, 5)]
    public decimal? VectorScore { get; set; }

    [Column("is_duplicate")]
    public bool? IsDuplicate { get; set; }

    [Column("threshold_used")]
    public string? ThresholdUsed { get; set; }

    [Column("matched_units", TypeName = "jsonb")]
    public string? MatchedUnits { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [ForeignKey("ExamId")]
    [InverseProperty("DuplicateDetections")]
    public virtual Exam Exam { get; set; } = null!;

    [ForeignKey("ExaminerId")]
    [InverseProperty("DuplicateDetections")]
    public virtual Examiner? Examiner { get; set; }

    [ForeignKey("SubmissionId1")]
    [InverseProperty("DuplicateDetectionSubmissionId1Navigations")]
    public virtual Submission SubmissionId1Navigation { get; set; } = null!;

    [ForeignKey("SubmissionId2")]
    [InverseProperty("DuplicateDetectionSubmissionId2Navigations")]
    public virtual Submission SubmissionId2Navigation { get; set; } = null!;
}
