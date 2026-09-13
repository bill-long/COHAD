namespace Web.Services
{
    /// <summary>
    /// Settings for the resident-facing Dues page, bound from the <c>Dues</c> configuration section.
    /// </summary>
    public sealed class DuesOptions
    {
        /// <summary>
        /// The email address residents send Zelle dues payments to. It is a personal mailbox, so it is
        /// deliberately not committed to source: supply it as the <c>Dues__ZelleEmail</c> app setting (or
        /// a user secret locally). Blank or unset hides the Zelle option on the Dues page entirely.
        /// </summary>
        public string ZelleEmail { get; set; }
    }
}
