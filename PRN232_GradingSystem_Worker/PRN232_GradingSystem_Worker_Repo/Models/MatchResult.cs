using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("match_result")]
public partial class MatchResult
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("exam_id")]
    public Guid ExamId { get; set; }

    [Column("src_unit_id")]
    public Guid SrcUnitId { get; set; }

    [Column("tgt_unit_id")]
    public Guid TgtUnitId { get; set; }

    [Column("score")]
    [Precision(6, 5)]
    public decimal? Score { get; set; }

    [Column("detail", TypeName = "jsonb")]
    public string? Detail { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [ForeignKey("ExamId")]
    [InverseProperty("MatchResults")]
    public virtual Exam Exam { get; set; } = null!;

    [ForeignKey("SrcUnitId")]
    [InverseProperty("MatchResultSrcUnits")]
    public virtual CodeUnit SrcUnit { get; set; } = null!;

    [ForeignKey("TgtUnitId")]
    [InverseProperty("MatchResultTgtUnits")]
    public virtual CodeUnit TgtUnit { get; set; } = null!;
}
