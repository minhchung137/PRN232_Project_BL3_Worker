using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PRN232_GradingSystem_Worker_Repo.Models;

[Table("examiner")]
[Index("Code", Name = "examiner_code_key", IsUnique = true)]
public partial class Examiner
{
    [Key]
    [Column("examiner_id")]
    public Guid ExaminerId { get; set; }

    [Column("code")]
    public string Code { get; set; } = null!;

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [InverseProperty("Examiner")]
    public virtual ICollection<DuplicateDetection> DuplicateDetections { get; set; } = new List<DuplicateDetection>();

    [InverseProperty("Examiner")]
    public virtual ICollection<Submission> Submissions { get; set; } = new List<Submission>();
}
