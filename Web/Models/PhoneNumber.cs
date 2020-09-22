using System;

namespace Web.Models
{
    public class PhoneNumber
    {
        public int AreaCode { get; set; }

        public int Prefix { get; set; }

        public int LineNumber { get; set; }

        public string Type { get; set; }

        public bool VisibleInDirectory { get; set; }
    }
}
