namespace Web.UpdateModels
{
    public class ForwardingSettingsUpdate
    {
        public bool ForwardingEnabled { get; set; }

        /// <summary>
        /// "DirectoryOnly" or "All". Parsed case-insensitively.
        /// </summary>
        public string ForwardingSenderFilter { get; set; }
    }
}
