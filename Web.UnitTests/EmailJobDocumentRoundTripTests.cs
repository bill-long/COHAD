using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;

namespace Web.UnitTests
{
    /// <summary>
    /// Locks that an <see cref="EmailJobRecipient"/> survives the trip to Cosmos and back.
    /// <para>
    /// This exists because the mock repositories hold the live object graph and never serialise, so
    /// a field missing from the hand-written mapper is invisible to every other test in this suite -
    /// the code reads and writes it happily in memory and silently loses it in production. That
    /// blind spot has produced two defects in this feature already: a Guid read that threw on every
    /// stored row, and a newly-added recipient field that was never persisted, which quietly turned
    /// an idempotency guarantee into a no-op.
    /// </para>
    /// <para>
    /// Driven by reflection rather than a fixed list of properties, so it fails for the <em>next</em>
    /// field somebody adds without touching the mapper. A hand-written assertion list would need the
    /// same discipline it is meant to enforce.
    /// </para>
    /// </summary>
    public class EmailJobDocumentRoundTripTests
    {
        [Fact]
        public void EveryRecipientPropertySurvivesACosmosRoundTrip()
        {
            var writable = typeof(EmailJobRecipient)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .ToList();

            Assert.NotEmpty(writable);

            var recipient = new EmailJobRecipient();
            foreach (var property in writable)
                property.SetValue(recipient, DistinctValueFor(property));

            var job = new EmailJob
            {
                Id = Guid.NewGuid(),
                Status = EmailJobStatus.InProgress,
                Category = "board",
                FromEmail = "board@cohad.org",
                FromDisplay = "COHAD Board",
                Subject = "Annual Meeting",
                Recipients = new List<EmailJobRecipient> { recipient },
            };

            // Through real JSON text: that is the step the mock repositories skip, and the step where
            // an unmapped or wrongly-typed field actually goes missing.
            var stored = JObject.Parse(CosmosLegacyDocumentMapper.ToEmailJobDocument(job).ToString());
            var readBack = CosmosLegacyDocumentMapper.ToEmailJob(stored).Recipients.Single();

            foreach (var property in writable)
            {
                Assert.Equal(
                    property.GetValue(recipient),
                    property.GetValue(readBack)
                );
            }
        }

        /// <summary>
        /// A non-default value for the property's type, so a field the mapper drops comes back as
        /// the default and fails the comparison. A default-valued field would round-trip "correctly"
        /// through a mapper that ignores it entirely.
        /// </summary>
        private static object DistinctValueFor(PropertyInfo property)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (type == typeof(string))
                return $"sample-{property.Name}";
            if (type == typeof(Guid))
                return Guid.NewGuid();
            if (type == typeof(int))
                return 7;
            if (type == typeof(bool))
                return true;
            if (type == typeof(DateTime))
                return new DateTime(2026, 8, 8, 12, 34, 56, DateTimeKind.Utc);
            if (type.IsEnum)
            {
                // The last declared value, which is never the default for these enums - a default
                // would round-trip through a mapper that never wrote the field.
                var values = Enum.GetValues(type);
                return values.GetValue(values.Length - 1)!;
            }

            throw new NotSupportedException(
                $"{property.Name} is a {type.Name}, which this test does not know how to populate. "
                    + "Add a case above - do not exclude the property, or the mapper stops being covered for it."
            );
        }
    }
}
