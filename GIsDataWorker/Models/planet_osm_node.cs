using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace GIsDataWorker.Models;

[Table("planet_osm_nodes")]
public partial class planet_osm_node
{
    [Key] // تعريف id كمفتاح أساسي
    [Column("id")]
    public long id { get; set; }

    [Column("lat")]
    public int lat { get; set; }

    [Column("lon")]
    public int lon { get; set; }

    // استخدام string لتمثيل الـ jsonb، 
    // أو يمكنك استخدام JsonDocument إذا كنت تريد التعامل معه ككائن JSON مباشرة
    [Column("tags", TypeName = "jsonb")]
    public string? tags { get; set; }
}