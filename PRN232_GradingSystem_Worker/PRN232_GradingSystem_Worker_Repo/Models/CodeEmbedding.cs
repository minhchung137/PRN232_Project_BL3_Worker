using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("code_embedding")]
public partial class CodeEmbedding
{
    [Key]
    [Column("unit_id")]
    public Guid UnitId { get; set; }

    [Column("submission_id")]
    public Guid SubmissionId { get; set; }

    [Column("emb")]
    public string Emb { get; set; } = null!;

    [Column("model_name")]
    public string ModelName { get; set; } = null!;

    [Column("embedding_dimension")]
    public int EmbeddingDimension { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [ForeignKey("SubmissionId")]
    [InverseProperty("CodeEmbeddings")]
    public virtual Submission Submission { get; set; } = null!;

    [ForeignKey("UnitId")]
    [InverseProperty("CodeEmbedding")]
    public virtual CodeUnit Unit { get; set; } = null!;
}
