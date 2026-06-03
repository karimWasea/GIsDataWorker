using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GIsDataWorker.Models;

[Table("osm2pgsql_properties")]
public partial class osm2pgsql_property
{
    [Key] // تعريف هذا العمود كمفتاح أساسي
    [Column("property")]
    public string property { get; set; } = null!;

    [Column("value")]
    public string value { get; set; } = null!;
}