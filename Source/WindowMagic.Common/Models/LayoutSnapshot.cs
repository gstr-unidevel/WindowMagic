using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WindowMagic.Common.Models
{
    class LayoutSnapshot
    {
        public DesktopDisplayMetrics DesktopMetrics { get; set; }
        public List<ApplicationDisplayMetrics> ApplicationWindows { get; set; }

    }
}
