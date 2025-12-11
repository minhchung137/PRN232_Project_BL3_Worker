using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("code_file")]
public partial class CodeFile
{
    [Key]
    [Column("file_id")]
    public Guid FileId { get; set; }

    [Column("submission_id")]
    public Guid SubmissionId { get; set; }

    [Column("rel_path")]
    public string RelPath { get; set; } = null!;

    [Column("language")]
    public string? Language { get; set; }

    [Column("line_count")]
    public int? LineCount { get; set; }

    [Column("file_hash")]
    public byte[]? FileHash { get; set; }

    [Column("file_size")]
    public long? FileSize { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [InverseProperty("File")]
    public virtual ICollection<CodeUnit> CodeUnits { get; set; } = new List<CodeUnit>();

    [ForeignKey("SubmissionId")]
    [InverseProperty("CodeFiles")]
    public virtual Submission Submission { get; set; } = null!;
}
