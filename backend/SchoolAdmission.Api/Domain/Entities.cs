using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SchoolAdmission.Api.Domain;

public static class HoldStatuses
{
    public const string Held = "HELD";
    public const string Confirmed = "CONFIRMED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
}

[Table("classes")]
public class SchoolClass
{
    [Key]
    [Column("id")]
    [MaxLength(64)]
    public string Id { get; set; } = string.Empty;

    [Required]
    [Column("name")]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [Column("total_seats")]
    public int TotalSeats { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<SeatHold> Holds { get; set; } = new();
}

[Table("holds")]
public class SeatHold
{
    [Key]
    [Column("hold_id")]
    [MaxLength(64)]
    public string HoldId { get; set; } = string.Empty;

    [Required]
    [Column("class_id")]
    [MaxLength(64)]
    public string ClassId { get; set; } = string.Empty;

    [Required]
    [Column("student_id")]
    [MaxLength(32)]
    public string StudentId { get; set; } = string.Empty;

    [Required]
    [Column("status")]
    [MaxLength(32)]
    public string Status { get; set; } = HoldStatuses.Held;

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ClassId))]
    public SchoolClass? SchoolClass { get; set; }
}
