using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GIsDataWorker.Models;

[Table("planet_osm_ways")]
public partial class planet_osm_way
{
    [Key] // مفتاح أساسي للجدول
    [Column("id")]
    public long id { get; set; }

    [Required] // العمود NOT NULL في قاعدة البيانات
    [Column("nodes", TypeName = "bigint[]")]
    public long[] nodes { get; set; } = null!;

    [Column("tags", TypeName = "jsonb")]
    public string? tags { get; set; }
}