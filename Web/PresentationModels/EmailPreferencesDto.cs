namespace Web.PresentationModels
{
    public class EmailPreferencesDto
    {
        public string Email { get; set; }

        public string HomeName { get; set; }

        public bool BoardEmailOptedIn { get; set; }

        public bool WelcomeEmailOptedIn { get; set; }

        public bool GardenClubEmailOptedIn { get; set; }

        public bool SocialCommitteeEmailOptedIn { get; set; }

        public bool SunshineCommitteeEmailOptedIn { get; set; }
    }
}
