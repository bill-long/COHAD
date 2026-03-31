namespace Web.PresentationModels
{
    /// <summary>
    /// DTO for updating email preferences. All fields are nullable so that
    /// omitted fields are left unchanged (partial update / PATCH semantics).
    /// </summary>
    public class UpdateEmailPreferencesDto
    {
        public bool? BoardEmailOptedIn { get; set; }

        public bool? WelcomeEmailOptedIn { get; set; }

        public bool? GardenClubEmailOptedIn { get; set; }

        public bool? SocialCommitteeEmailOptedIn { get; set; }

        public bool? SunshineCommitteeEmailOptedIn { get; set; }
    }
}
