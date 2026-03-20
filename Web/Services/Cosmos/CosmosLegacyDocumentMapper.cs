using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Web.Models;

namespace Web.Services.Cosmos
{
    internal static class CosmosLegacyDocumentMapper
    {
        internal static string ToUserDocumentId(string uniqueId) =>
            uniqueId.StartsWith("User|", StringComparison.Ordinal) ? uniqueId : $"User|{uniqueId}";

        internal static string ToHomeDocumentId(Guid homeId) => $"Home|{homeId:D}";
        internal static string ToPaymentDocumentId(Guid paymentId) => $"Payment|{paymentId:D}";
        internal static string ToAuditLogDocumentId(Guid auditLogId) => $"AuditLog|{auditLogId:D}";

        internal static Guid ParseLegacyGuid(string raw)
        {
            if (Guid.TryParse(raw, out var direct))
            {
                return direct;
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var split = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
                var tail = split.Length > 0 ? split[split.Length - 1] : raw;
                if (Guid.TryParse(tail, out var legacy))
                {
                    return legacy;
                }
            }

            throw new FormatException($"Could not parse GUID value '{raw}'.");
        }

        internal static T DeserializeFlexible<T>(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return default;
            }

            if (token.Type == JTokenType.String)
            {
                var raw = token.Value<string>();
                return string.IsNullOrWhiteSpace(raw) ? default : JsonConvert.DeserializeObject<T>(raw);
            }

            return token.ToObject<T>();
        }

        internal static User ToUser(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            var uniqueId = rawId != null && rawId.StartsWith("User|", StringComparison.Ordinal)
                ? rawId.Substring("User|".Length)
                : doc.Value<string>("UniqueId") ?? rawId;

            return new User
            {
                UniqueId = uniqueId,
                NameIdentifier = doc.Value<string>("NameIdentifier"),
                GivenName = doc.Value<string>("GivenName"),
                Surname = doc.Value<string>("Surname"),
                IdentityProvider = doc.Value<string>("IdentityProvider"),
                Emails = doc.Value<string>("Emails"),
                StreetAddress = doc.Value<string>("StreetAddress"),
                LastLoggedIn = doc["LastLoggedIn"]?.ToObject<DateTime?>(),
                Roles = DeserializeFlexible<List<User.Role>>(doc["Roles"]) ?? new List<User.Role>(),
                OwnedHomeIds = DeserializeFlexible<List<Guid>>(doc["OwnedHomeIds"]) ?? new List<Guid>()
            };
        }

        internal static JObject ToUserDocument(User user)
        {
            var doc = new JObject();
            MergeUserIntoDocument(doc, user);
            return doc;
        }

        /// <summary>
        /// Updates a user document in-place so extra properties from Cosmos (e.g. AuditLog from EF) are preserved
        /// across upserts.
        /// </summary>
        internal static void MergeUserIntoDocument(JObject doc, User user)
        {
            doc["id"] = ToUserDocumentId(user.UniqueId);
            doc["Discriminator"] = "User";
            doc["UniqueId"] = user.UniqueId;
            doc["NameIdentifier"] = user.NameIdentifier;
            doc["GivenName"] = user.GivenName;
            doc["Surname"] = user.Surname;
            doc["IdentityProvider"] = user.IdentityProvider;
            doc["Emails"] = user.Emails;
            doc["StreetAddress"] = user.StreetAddress;
            doc["LastLoggedIn"] = user.LastLoggedIn != null ? JToken.FromObject(user.LastLoggedIn) : JValue.CreateNull();
            // Keep legacy storage format for backward compatibility.
            doc["Roles"] = JsonConvert.SerializeObject(user.Roles ?? new List<User.Role>());
            doc["OwnedHomeIds"] = JsonConvert.SerializeObject(user.OwnedHomeIds ?? new List<Guid>());
        }

        internal static Home ToHome(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new Home
            {
                Id = ParseLegacyGuid(rawId),
                StreetNumber = doc.Value<int?>("StreetNumber") ?? 0,
                StreetName = doc.Value<string>("StreetName"),
                PhoneNumber = DeserializeFlexible<PhoneNumber>(doc["PhoneNumber"]),
                EmailAddress = DeserializeFlexible<EmailAddress>(doc["EmailAddress"]),
                Residents = DeserializeFlexible<List<Resident>>(doc["Residents"]) ?? new List<Resident>()
            };
        }

        internal static JObject ToHomeDocument(Home home)
        {
            var doc = new JObject();
            MergeHomeIntoDocument(doc, home);
            return doc;
        }

        /// <summary>
        /// Updates home JSON in-place so EF-era properties we do not model (e.g. UserUniqueId) are preserved on upsert.
        /// </summary>
        internal static void MergeHomeIntoDocument(JObject doc, Home home)
        {
            doc["id"] = ToHomeDocumentId(home.Id);
            doc["Discriminator"] = "Home";
            doc["Id"] = home.Id.ToString("D");
            doc["StreetNumber"] = home.StreetNumber;
            doc["StreetName"] = home.StreetName;
            // Keep legacy storage format for backward compatibility.
            doc["PhoneNumber"] = JsonConvert.SerializeObject(home.PhoneNumber);
            doc["EmailAddress"] = JsonConvert.SerializeObject(home.EmailAddress);
            doc["Residents"] = JsonConvert.SerializeObject(home.Residents ?? new List<Resident>());
        }

        internal static Payment ToPayment(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new Payment
            {
                Id = ParseLegacyGuid(rawId),
                PayerUniqueId = doc.Value<string>("PayerUniqueId"),
                PayerEmail = doc.Value<string>("PayerEmail"),
                PayerName = doc.Value<string>("PayerName"),
                Amount = doc.Value<string>("Amount"),
                Date = doc["Date"]?.ToObject<DateTime?>(),
                PaymentType = doc["PaymentType"]?.ToObject<Payment.Type>() ?? Payment.Type.OneTime,
                FullDetailsJSON = doc.Value<string>("FullDetailsJSON")
            };
        }

        internal static JObject ToPaymentDocument(Payment payment)
        {
            return new JObject
            {
                ["id"] = ToPaymentDocumentId(payment.Id),
                ["PayerUniqueId"] = payment.PayerUniqueId,
                ["PayerEmail"] = payment.PayerEmail,
                ["PayerName"] = payment.PayerName,
                ["Amount"] = payment.Amount,
                ["Date"] = payment.Date != null ? JToken.FromObject(payment.Date) : JValue.CreateNull(),
                ["PaymentType"] = JToken.FromObject(payment.PaymentType),
                ["FullDetailsJSON"] = payment.FullDetailsJSON
            };
        }

        internal static NewAuditLogEntry ToAuditLog(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new NewAuditLogEntry
            {
                Id = ParseLegacyGuid(rawId),
                Time = doc["Time"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                UserId = doc.Value<string>("UserId"),
                UserDisplayName = doc.Value<string>("UserDisplayName"),
                SubjectId = doc.Value<string>("SubjectId"),
                SubjectName = doc.Value<string>("SubjectName"),
                Action = doc.Value<string>("Action")
            };
        }

        internal static JObject ToAuditLogDocument(NewAuditLogEntry audit)
        {
            return new JObject
            {
                ["id"] = ToAuditLogDocumentId(audit.Id),
                ["Time"] = JToken.FromObject(audit.Time),
                ["UserId"] = audit.UserId,
                ["UserDisplayName"] = audit.UserDisplayName,
                ["SubjectId"] = audit.SubjectId,
                ["SubjectName"] = audit.SubjectName,
                ["Action"] = audit.Action
            };
        }
    }
}
