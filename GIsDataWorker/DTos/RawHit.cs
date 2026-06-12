using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.DTos
{
    public sealed class RawHit
    {
        public long? OsmId { get; set; }
        public string? Name { get; set; }
        public string? Amenity { get; set; }
        public string? Tourism { get; set; }
        public string? Leisure { get; set; }
        public string? Shop { get; set; }
        public string? HistoricTag { get; set; }
        public bool IsPolygon { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }
}
