using System;
using System.Collections.Generic;
using System.Text;

namespace Web.Models
{
    /// <summary>
    /// Human-readable URL segments for blog posts: <c>{year}-{slugified-title}</c>, with numeric suffixes when needed.
    /// </summary>
    public static class BlogUrlSlug
    {
        public static string BuildBaseSlug(DateTime publishUtc, string title)
        {
            var year = publishUtc.Year;
            var name = SlugifyTitle(title);
            return $"{year}-{name}";
        }

        public static string SlugifyTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "post";
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

            return sb.Length == 0 ? "post" : sb.ToString();
        }

        public static string ResolveUrlSegment(BlogPost post)
        {
            if (post == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(post.PublicSlug))
            {
                return post.PublicSlug.ToLowerInvariant();
            }

            return BuildBaseSlug(post.PublishUtc, post.Title);
        }

        public static string EnsureUniquePublicSlug(Guid id, DateTime publishUtc, string title, IEnumerable<BlogPost> allPosts)
        {
            var baseSlug = BuildBaseSlug(publishUtc, title);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in allPosts)
            {
                if (p.Id == id)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(p.PublicSlug))
                {
                    taken.Add(p.PublicSlug.ToLowerInvariant());
                }
                else
                {
                    taken.Add(BuildBaseSlug(p.PublishUtc, p.Title));
                }

                if (p.PreviousSlugs != null)
                {
                    foreach (var prev in p.PreviousSlugs)
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
