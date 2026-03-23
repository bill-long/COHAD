using System.Collections.Generic;

namespace Web.PresentationModels
{
    public class ManageEventsPayload
    {
        public List<CommunityEventDetail> Upcoming { get; set; } = new List<CommunityEventDetail>();

        public List<CommunityEventDetail> Past { get; set; } = new List<CommunityEventDetail>();
    }
}
