using Microsoft.EntityFrameworkCore;
using SchoolAdmission.Api.Domain;

namespace SchoolAdmission.Api.Data;

public class AdmissionDbContext : DbContext
{
    public AdmissionDbContext(DbContextOptions<AdmissionDbContext> options) : base(options)
    {
    }

    public DbSet<SchoolClass> Classes => Set<SchoolClass>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SchoolClass>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(256);
            entity.Property(c => c.TotalSeats).IsRequired();
            entity.HasIndex(c => c.CreatedAt);
        });

        modelBuilder.Entity<SeatHold>(entity =>
        {
            entity.HasKey(h => h.HoldId);
            entity.Property(h => h.ClassId).IsRequired().HasMaxLength(64);
            entity.Property(h => h.StudentId).IsRequired().HasMaxLength(32);
            entity.Property(h => h.Status).IsRequired().HasMaxLength(32);

            entity.HasOne(h => h.SchoolClass)
                .WithMany(c => c.Holds)
                .HasForeignKey(h => h.ClassId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(h => new { h.ClassId, h.Status, h.ExpiresAt });
            entity.HasIndex(h => new { h.StudentId, h.ClassId });
            entity.HasIndex(h => new { h.Status, h.ExpiresAt });
        });
    }
}

