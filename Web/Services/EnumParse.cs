using System;

namespace Web.Services
{
    /// <summary>
    /// Enum parsing helpers for the committee-mail spam-classification code.
    /// </summary>
    internal static class EnumParse
    {
        /// <summary>
        /// Parses <paramref name="value"/> to <typeparamref name="TEnum"/> only when it names a defined
        /// member (case-insensitive). Unlike <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
        /// alone, out-of-range numeric strings such as "999" fail here rather than producing an undefined
        /// enum value that could pass a later comparison (e.g. a spam-confidence threshold check).
        /// </summary>
        public static bool TryParseDefined<TEnum>(string? value, out TEnum result)
            where TEnum : struct, Enum
        {
            if (Enum.TryParse(value, ignoreCase: true, out result)
                && Enum.IsDefined(typeof(TEnum), result))
            {
                return true;
            }

            result = default;
            return false;
        }
    }
}
