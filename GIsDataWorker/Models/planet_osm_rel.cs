using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GIsDataWorker.Models;

[Table("planet_osm_rels")]
public partial class planet_osm_rel
{
    [Key] // معرف كمفتاح أساسي كما في الـ Schema
    [Column("id")]
    public long id { get; set; }

    [Required] // لأن العمود NOT NULL في قاعدة البيانات
    [Column("members", TypeName = "jsonb")]
    public string members { get; set; } = null!;

    [Column("tags", TypeName = "jsonb")]
    public string? tags { get; set; }
}