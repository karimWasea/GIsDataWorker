using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace GIsDataWorker.Models;

[Table("planet_osm_point")]
public partial class planet_osm_point
{
    public long? osm_id { get; set; }

    public string? access { get; set; }

    [Column("addr:housename")]
    public string? addr_housename { get; set; }

    [Column("addr:housenumber")]
    public string? addr_housenumber { get; set; }

    [Column("addr:interpolation")]
    public string? addr_interpolation { get; set; }

    public string? admin_level { get; set; }
    public string? aerialway { get; set; }
    public string? aeroway { get; set; }
    public string? amenity { get; set; }
    public string? area { get; set; }
    public string? barrier { get; set; }
    public string? bicycle { get; set; }
    public string? brand { get; set; }
    public string? bridge { get; set; }
    public string? boundary { get; set; }
    public string? building { get; set; }
    public string? capital { get; set; }
    public string? construction { get; set; }
    public string? covered { get; set; }
    public string? culvert { get; set; }
    public string? cutting { get; set; }
    public string? denomination { get; set; }
    public string? disused { get; set; }
    public string? ele { get; set; }
    public string? embankment { get; set; }
    public string? foot { get; set; }

    [Column("generator:source")]
    public string? generator_source { get; set; }

    public string? harbour { get; set; }
    public string? highway { get; set; }
    public string? historic { get; set; }
    public string? horse { get; set; }
    public string? intermittent { get; set; }
    public string? junction { get; set; }
    public string? landuse { get; set; }
    public string? layer { get; set; }
    public string? leisure { get; set; }

    [Column("lock")]
    public string? _lock { get; set; }

    public string? man_made { get; set; }
    public string? military { get; set; }
    public string? motorcar { get; set; }
    public string? name { get; set; }

    [Column("natural")]
    public string? natural { get; set; }

    public string? office { get; set; }
    public string? oneway { get; set; }

    [Column("operator")]
    public string? _operator { get; set; }

    public string? place { get; set; }
    public string? population { get; set; }
    public string? power { get; set; }
    public string? power_source { get; set; }
    public string? public_transport { get; set; }
    public string? railway { get; set; }

    [Column("ref")]
    public string? _ref { get; set; }

    public string? religion { get; set; }
    public string? route { get; set; }
    public string? service { get; set; }
    public string? shop { get; set; }
    public string? sport { get; set; }
    public string? surface { get; set; }
    public string? toll { get; set; }
    public string? tourism { get; set; }

    [Column("tower:type")]
    public string? tower_type { get; set; }

    public string? tunnel { get; set; }
    public string? water { get; set; }
    public string? waterway { get; set; }
    public string? wetland { get; set; }
    public string? width { get; set; }
    public string? wood { get; set; }
    public int? z_order { get; set; }

    [Column("way")]
    public Point? way { get; set; }
}