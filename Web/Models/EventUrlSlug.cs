using System;
using System.Collections.Generic;
using System.Text;

namespace Web.Models
{
    /// <summary>
    /// Human-readable URL segments for events: <c>{year}-{slugified-title}</c>, with numeric suffixes when needed.
    /// </summary>
    public static class EventUrlSlug
    {
        public static string BuildBaseSlug(DateTime startUtc, string title)
        {
            var year = startUtc.Year;
            var name = SlugifyTitle(title);
            return $"{year}-{name}";
        }

        public static string SlugifyTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "event";
            }

            var lower = title.Trim().ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (var c in lower)
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    sb.Append(c);
                }
                else if (c is ' ' or '-' or '_')
                {
                    if (sb.Length > 0 && sb[^1] != '-')
                    {
                        sb.Append('-');
                    }
                }
            }

            while (sb.Length > 0 && sb[0] == '-')
            {
                sb.Remove(0, 1);
            }

            while (sb.Length > 0 && sb[^1] == '-')
            {
                sb.Length--;
            }

            return sb.Length == 0 ? "event" : sb.ToString();
        }

        /// <summary>
        /// Segment used in public URLs and API paths (persisted when possible; otherwise derived for legacy rows).
        /// </summary>
        public static string ResolveUrlSegment(CommunityEvent e)
        {
            if (e == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(e.PublicSlug))
            {
                return e.PublicSlug.ToLowerInvariant();
            }

            return BuildBaseSlug(e.StartUtc, e.Title);
        }

        /// <summary>
        /// Assigns a unique <see cref="CommunityEvent.PublicSlug"/> among all other events.
        /// </summary>
        public static string EnsureUniquePublicSlug(Guid id, DateTime startUtc, string title, IEnumerable<CommunityEvent> allEvents)
        {
            var baseSlug = BuildBaseSlug(startUtc, title);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in allEvents)
            {
                if (e.Id == id)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(e.PublicSlug))
                {
                    taken.Add(e.PublicSlug.ToLowerInvariant());
                }
                else
                {
                    taken.Add(BuildBaseSlug(e.StartUtc, e.Title));
                }

                if (e.PreviousSlugs != null)
                {
                    foreach (var prev in e.PreviousSlugs)
                    {
                        if (string.IsNullOrWhiteSpace(prev))
                        {
                            continue;
                        }

                        taken.Add(prev.Trim().ToLowerInvariant());
                    }
                }
            }

            if (!taken.Contains(baseSlug))
            {
                return baseSlug;
            }

            for (var i = 2; i < 10_000; i++)
            {
                var candidate = $"{baseSlug}-{i}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }

            return $"{baseSlug}-{Guid.NewGuid():N}";
        }
    }
}
