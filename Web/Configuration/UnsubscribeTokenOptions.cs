using System;

namespace Web.Configuration
{
    public class UnsubscribeTokenOptions
    {
        /// <summary>
        /// The key the legacy <c>?token=</c> scheme was generated with.
        /// <para>
        /// Nothing generates tokens any more - short links replaced them - so this is now a
        /// validation-only input, kept so the links already sitting in people's inboxes keep working
        /// until they expire. See <see cref="LegacySigningKey"/> for where it should actually live
        /// after the cutover.
        /// </para>
        /// </summary>
        public string SigningKey { get; set; }

        /// <summary>
        /// The legacy key, moved here at cutover so its role is stated by the configuration rather
        /// than inferred. When set it is used for validation in place of <see cref="SigningKey"/>.
        /// <para>
        /// The move is a rotation, not a rename: the production key leaked into a transcript on
        /// 2026-08-06. What the rotation buys is bounded and worth being honest about - this key can
        /// still mint payloads the legacy validator accepts, and it has to, because the links
        /// already delivered were minted with it. <see cref="LegacyCutoverUtc"/> is what contains
        /// that, and the exposure ends when the last genuine legacy link expires.
        /// </para>
        /// </summary>
        public string LegacySigningKey { get; set; }

        /// <summary>
        /// When short links took over. A legacy token claiming to have been issued after this is
        /// rejected: nothing generated one after the cutover, so a later timestamp is either a
        /// forgery or a clock problem, and neither should authorise anything.
        /// <para>
        /// This does not stop a determined forger, who controls the timestamp and can simply claim
        /// an earlier one. What it does is bound the leaked key's useful life to the legacy expiry
        /// window instead of leaving it open-ended, and keep genuine traffic honest so the
        /// retirement counter means what it says.
        /// </para>
        /// <para>
        /// Unset means no cutover check, which is the correct default for an environment that has
        /// not cut over yet. Set it at cutover.
        /// </para>
        /// </summary>
        public DateTimeOffset? LegacyCutoverUtc { get; set; }
    }
}
