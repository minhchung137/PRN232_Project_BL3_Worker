using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("exam")]
[Index("Code", Name = "exam_code_key", IsUnique = true)]
public partial class Exam
{
    [Key]
    [Column("exam_id")]
    public Guid ExamId { get; set; }

    [Column("code")]
    public string Code { get; set; } = null!;

    [Column("title")]
    public string? Title { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [InverseProperty("Exam")]
    public virtual ICollection<DuplicateDetection> DuplicateDetections { get; set; } = new List<DuplicateDetection>();

    [InverseProperty("Exam")]
    public virtual ICollection<MatchResult> MatchResults { get; set; } = new List<MatchResult>();

    [InverseProperty("Exam")]
    public virtual ICollection<Submission> Submissions { get; set; } = new List<Submission>();
}
