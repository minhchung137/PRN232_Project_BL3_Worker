using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("code_unit")]
public partial class CodeUnit
{
    [Key]
    [Column("unit_id")]
    public Guid UnitId { get; set; }

    [Column("submission_id")]
    public Guid SubmissionId { get; set; }

    [Column("file_id")]
    public Guid FileId { get; set; }

    [Column("unit_kind")]
    public string UnitKind { get; set; } = null!;

    [Column("unit_key")]
    public string UnitKey { get; set; } = null!;

    [Column("start_line")]
    public int? StartLine { get; set; }

    [Column("end_line")]
    public int? EndLine { get; set; }

    [Column("content")]
    public string? Content { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [InverseProperty("Unit")]
    public virtual CodeEmbedding? CodeEmbedding { get; set; }

    [ForeignKey("FileId")]
    [InverseProperty("CodeUnits")]
    public virtual CodeFile File { get; set; } = null!;

    [InverseProperty("SrcUnit")]
    public virtual ICollection<MatchResult> MatchResultSrcUnits { get; set; } = new List<MatchResult>();

    [InverseProperty("TgtUnit")]
    public virtual ICollection<MatchResult> MatchResultTgtUnits { get; set; } = new List<MatchResult>();

    [ForeignKey("SubmissionId")]
    [InverseProperty("CodeUnits")]
    public virtual Submission Submission { get; set; } = null!;
}
