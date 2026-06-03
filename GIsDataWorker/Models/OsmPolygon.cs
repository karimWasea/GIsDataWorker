using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.Models
{
    using NetTopologySuite.Geometries;
    using System.ComponentModel.DataAnnotations.Schema;

    [Table("planet_osm_polygon")] // الجدول الذي أنشأه osm2pgsql
    public class OsmPolygon
    {
        public long Osm_Id { get; set; }
        public string Name { get; set; }

        [Column("way")]
        public Geometry Way { get; set; } // هذا هو الحقل الجغرافي
    }
}
