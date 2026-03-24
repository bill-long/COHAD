using System;
using System.Collections.Generic;
using System.Linq;

namespace Web.Utils
{
    internal static class StringListHelper
    {
        internal static List<string> NormalizeStringList(IEnumerable<string> values) =>
            (values ?? Enumerable.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
