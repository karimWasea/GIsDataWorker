using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.DTos
{
    public class RegionResultDtoDto
     {
        public string? Name { get; set; }
        public string? AdminLevel { get; set; }  // was: public int AdminLevel { get; set; }
                                              // public string? Boundary { get; set; }
        public string? Place { get; set; }
        public string? Suburb { get; set; }
        public string? OsmId { get; set; }
        public string? Boundary { get; internal set; }
    }
}
