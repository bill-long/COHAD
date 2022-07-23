using System;

namespace Web.UpdateModels
{
    public class UpdatedPrintableDirectory
    {
        public Guid Id { get; set; }

        public string FrontCoverDataUrl { get; set; }

        public string TitlePageHTML { get; set; }

        public string IntroductionHTML { get; set; }

        public string BackCoverDataUrl { get; set; }
    }
}
