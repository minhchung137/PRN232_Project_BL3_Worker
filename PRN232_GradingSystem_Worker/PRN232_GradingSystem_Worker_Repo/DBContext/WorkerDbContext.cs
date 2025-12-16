using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using PRN232_GradingSystem_Worker_Repo.Models;

namespace PRN232_GradingSystem_Worker_Repo.DBContext;

public partial class WorkerDbContext : DbContext
{
    public WorkerDbContext()
    {
    }

    public WorkerDbContext(DbContextOptions<WorkerDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<CodeEmbedding> CodeEmbeddings { get; set; }

    public virtual DbSet<CodeFile> CodeFiles { get; set; }

    public virtual DbSet<CodeUnit> CodeUnits { get; set; }

    public virtual DbSet<DuplicateDetection> DuplicateDetections { get; set; }

    public virtual DbSet<Exam> Exams { get; set; }

    public virtual DbSet<Examiner> Examiners { get; set; }

    public virtual DbSet<MatchResult> MatchResults { get; set; }

    public virtual DbSet<Submission> Submissions { get; set; }

//    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
//#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
//        => optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=prn232_worker_db;Username=postgres;Password=12345");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<CodeEmbedding>(entity =>
        {
            entity.HasKey(e => e.UnitId).HasName("code_embedding_pkey");

            entity.Property(e => e.UnitId).ValueGeneratedNever();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Submission).WithMany(p => p.CodeEmbeddings).HasConstraintName("code_embedding_submission_id_fkey");

            entity.HasOne(d => d.Unit).WithOne(p => p.CodeEmbedding).HasConstraintName("code_embedding_unit_id_fkey");
        });

        modelBuilder.Entity<CodeFile>(entity =>
        {
            entity.HasKey(e => e.FileId).HasName("code_file_pkey");

            entity.Property(e => e.FileId).HasDefaultValueSql("uuid_generate_v4()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Submission).WithMany(p => p.CodeFiles).HasConstraintName("code_file_submission_id_fkey");
        });

        modelBuilder.Entity<CodeUnit>(entity =>
        {
            entity.HasKey(e => e.UnitId).HasName("code_unit_pkey");

            entity.Property(e => e.UnitId).HasDefaultValueSql("uuid_generate_v4()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.File).WithMany(p => p.CodeUnits).HasConstraintName("code_unit_file_id_fkey");

            entity.HasOne(d => d.Submission).WithMany(p => p.CodeUnits).HasConstraintName("code_unit_submission_id_fkey");
        });

        modelBuilder.Entity<DuplicateDetection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("duplicate_detection_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.IsDuplicate).HasDefaultValue(false);

            entity.HasOne(d => d.Exam).WithMany(p => p.DuplicateDetections)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("duplicate_detection_exam_id_fkey");

            entity.HasOne(d => d.Examiner).WithMany(p => p.DuplicateDetections).HasConstraintName("duplicate_detection_examiner_id_fkey");

            entity.HasOne(d => d.SubmissionId1Navigation).WithMany(p => p.DuplicateDetectionSubmissionId1Navigations).HasConstraintName("duplicate_detection_submission_id1_fkey");

            entity.HasOne(d => d.SubmissionId2Navigation).WithMany(p => p.DuplicateDetectionSubmissionId2Navigations).HasConstraintName("duplicate_detection_submission_id2_fkey");
        });

        modelBuilder.Entity<Exam>(entity =>
        {
            entity.HasKey(e => e.ExamId).HasName("exam_pkey");

            entity.Property(e => e.ExamId).HasDefaultValueSql("uuid_generate_v4()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Examiner>(entity =>
        {
            entity.HasKey(e => e.ExaminerId).HasName("examiner_pkey");

            entity.Property(e => e.ExaminerId).HasDefaultValueSql("uuid_generate_v4()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<MatchResult>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("match_result_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Exam).WithMany(p => p.MatchResults)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("match_result_exam_id_fkey");

            entity.HasOne(d => d.SrcUnit).WithMany(p => p.MatchResultSrcUnits)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("match_result_src_unit_id_fkey");

            entity.HasOne(d => d.TgtUnit).WithMany(p => p.MatchResultTgtUnits)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("match_result_tgt_unit_id_fkey");
        });

        modelBuilder.Entity<Submission>(entity =>
        {
            entity.HasKey(e => e.SubmissionId).HasName("submission_pkey");

            entity.Property(e => e.SubmissionId).HasDefaultValueSql("uuid_generate_v4()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Status).HasDefaultValueSql("'processed'::text");
            entity.Property(e => e.TotalFiles).HasDefaultValue(0);
            entity.Property(e => e.TotalLines).HasDefaultValue(0);

            entity.HasOne(d => d.Exam).WithMany(p => p.Submissions)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("submission_exam_id_fkey");

            entity.HasOne(d => d.Examiner).WithMany(p => p.Submissions).HasConstraintName("submission_examiner_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
