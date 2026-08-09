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

        internal static string ToResidentDocumentId(Guid documentId) => $"ResidentDocument|{documentId:D}";

        internal static string ToDocumentFolderId(Guid folderId) => $"DocumentFolder|{folderId:D}";

        internal static string ToCommunityEventId(Guid eventId) => $"CommunityEvent|{eventId:D}";

        internal static string ToEmailJobDocumentId(Guid jobId) => $"EmailJob|{jobId:D}";

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
            var uniqueId =
                rawId != null && rawId.StartsWith("User|", StringComparison.Ordinal)
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
                UnassociatedSinceUtc = doc["UnassociatedSinceUtc"]?.ToObject<DateTime?>(),
                NoRolesSinceUtc = doc["NoRolesSinceUtc"]?.ToObject<DateTime?>(),
                Roles = DeserializeFlexible<List<User.Role>>(doc["Roles"]) ?? new List<User.Role>(),
                OwnedHomeIds = DeserializeFlexible<List<Guid>>(doc["OwnedHomeIds"]) ?? new List<Guid>(),
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
            doc["LastLoggedIn"] =
                user.LastLoggedIn != null ? JToken.FromObject(user.LastLoggedIn) : JValue.CreateNull();
            doc["UnassociatedSinceUtc"] =
                user.UnassociatedSinceUtc != null ? JToken.FromObject(user.UnassociatedSinceUtc) : JValue.CreateNull();
            doc["NoRolesSinceUtc"] =
                user.NoRolesSinceUtc != null ? JToken.FromObject(user.NoRolesSinceUtc) : JValue.CreateNull();
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
                // Residents are now stored in a separate "Residents" container.
                // Controllers populate Home.Residents from IResidentRepository when needed.
                ETag = doc.Value<string>("_etag"),
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
            // Residents are now stored in a separate "Residents" container.
            // Preserve any existing "Residents" field in the document so that a rollback
            // to the previous code version can still read them until migration cleanup.
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
                FullDetailsJSON = doc.Value<string>("FullDetailsJSON"),
                PayPalTransactionId = doc.Value<string>("PayPalTransactionId"),
                HomeId = ParseNullableGuid(doc["HomeId"]),
            };
        }

        internal static JObject ToPaymentDocument(Payment payment)
        {
            return new JObject
            {
                ["id"] = ToPaymentDocumentId(payment.Id),
                ["PayerUniqueId"] = payment.PayerUniqueId != null ? payment.PayerUniqueId : JValue.CreateNull(),
                ["PayerEmail"] = payment.PayerEmail,
                ["PayerName"] = payment.PayerName,
                ["Amount"] = payment.Amount,
                ["Date"] = payment.Date != null ? JToken.FromObject(payment.Date) : JValue.CreateNull(),
                ["PaymentType"] = JToken.FromObject(payment.PaymentType),
                ["FullDetailsJSON"] = payment.FullDetailsJSON,
                ["PayPalTransactionId"] =
                    payment.PayPalTransactionId != null ? payment.PayPalTransactionId : JValue.CreateNull(),
                ["HomeId"] = payment.HomeId != null ? payment.HomeId.Value.ToString("D") : JValue.CreateNull(),
            };
        }

        private static Guid? ParseNullableGuid(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var s = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return Guid.TryParse(s, out var g) ? g : null;
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
                Action = doc.Value<string>("Action"),
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
                ["Action"] = audit.Action,
            };
        }

        internal static ResidentDocument ToResidentDocument(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new ResidentDocument
            {
                Id = ParseLegacyGuid(rawId),
                DisplayName = doc.Value<string>("DisplayName"),
                OriginalFileName = doc.Value<string>("OriginalFileName"),
                BlobPath = doc.Value<string>("BlobPath"),
                ContentType = doc.Value<string>("ContentType"),
                SizeBytes = doc.Value<long?>("SizeBytes") ?? 0,
                UploadedByUniqueId = doc.Value<string>("UploadedByUniqueId"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                FolderId = ParseNullableGuid(doc["FolderId"]),
            };
        }

        internal static JObject ToResidentDocumentDocument(ResidentDocument document)
        {
            return new JObject
            {
                ["id"] = ToResidentDocumentId(document.Id),
                ["DisplayName"] = document.DisplayName,
                ["OriginalFileName"] = document.OriginalFileName,
                ["BlobPath"] = document.BlobPath,
                ["ContentType"] = document.ContentType,
                ["SizeBytes"] = document.SizeBytes,
                ["UploadedByUniqueId"] = document.UploadedByUniqueId,
                ["CreatedUtc"] = JToken.FromObject(document.CreatedUtc),
                ["FolderId"] = document.FolderId != null ? document.FolderId.Value.ToString("D") : JValue.CreateNull(),
            };
        }

        internal static DocumentFolder ToDocumentFolder(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new DocumentFolder
            {
                Id = ParseLegacyGuid(rawId),
                Name = doc.Value<string>("Name"),
                SortOrder = doc.Value<int?>("SortOrder") ?? 0,
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
            };
        }

        internal static JObject ToDocumentFolderDocument(DocumentFolder folder)
        {
            return new JObject
            {
                ["id"] = ToDocumentFolderId(folder.Id),
                ["Name"] = folder.Name,
                ["SortOrder"] = folder.SortOrder,
                ["CreatedUtc"] = JToken.FromObject(folder.CreatedUtc),
            };
        }

        internal static CommunityEvent ToCommunityEvent(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new CommunityEvent
            {
                Id = ParseLegacyGuid(rawId),
                PublicSlug = doc.Value<string>("PublicSlug"),
                PreviousSlugs = doc["PreviousSlugs"]?.ToObject<List<string>>() ?? new List<string>(),
                Title = doc.Value<string>("Title"),
                Description = doc.Value<string>("Description"),
                StartUtc = doc["StartUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                AllowSignups = doc["AllowSignups"]?.ToObject<bool>() ?? false,
                SignupMode = doc["SignupMode"]?.ToObject<EventSignupMode>() ?? EventSignupMode.AdultsAndChildren,
                PromoMediaBlobPath = doc.Value<string>("PromoMediaBlobPath"),
                PromoMediaDisplayName = doc.Value<string>("PromoMediaDisplayName"),
                PromoMediaContentType = doc.Value<string>("PromoMediaContentType"),
                PromoMediaSizeBytes = doc["PromoMediaSizeBytes"]?.ToObject<long?>(),
                PromoMediaThumbBlobPath = doc.Value<string>("PromoMediaThumbBlobPath"),
                CreatedByUniqueId = doc.Value<string>("CreatedByUniqueId"),
                ModifiedByUniqueId = doc.Value<string>("ModifiedByUniqueId"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ModifiedUtc = doc["ModifiedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                Signups = DeserializeFlexible<List<EventSignup>>(doc["Signups"]) ?? new List<EventSignup>(),
            };
        }

        internal static JObject ToCommunityEventDocument(CommunityEvent communityEvent)
        {
            return new JObject
            {
                ["id"] = ToCommunityEventId(communityEvent.Id),
                ["PublicSlug"] = communityEvent.PublicSlug != null ? communityEvent.PublicSlug : JValue.CreateNull(),
                ["PreviousSlugs"] = JToken.FromObject(communityEvent.PreviousSlugs ?? new List<string>()),
                ["Title"] = communityEvent.Title,
                ["Description"] = communityEvent.Description,
                ["StartUtc"] = JToken.FromObject(communityEvent.StartUtc),
                ["AllowSignups"] = communityEvent.AllowSignups,
                ["SignupMode"] = JToken.FromObject(communityEvent.SignupMode),
                ["PromoMediaBlobPath"] =
                    communityEvent.PromoMediaBlobPath != null ? communityEvent.PromoMediaBlobPath : JValue.CreateNull(),
                ["PromoMediaDisplayName"] =
                    communityEvent.PromoMediaDisplayName != null
                        ? communityEvent.PromoMediaDisplayName
                        : JValue.CreateNull(),
                ["PromoMediaContentType"] =
                    communityEvent.PromoMediaContentType != null
                        ? communityEvent.PromoMediaContentType
                        : JValue.CreateNull(),
                ["PromoMediaSizeBytes"] =
                    communityEvent.PromoMediaSizeBytes != null
                        ? JToken.FromObject(communityEvent.PromoMediaSizeBytes)
                        : JValue.CreateNull(),
                ["PromoMediaThumbBlobPath"] =
                    communityEvent.PromoMediaThumbBlobPath != null
                        ? communityEvent.PromoMediaThumbBlobPath
                        : JValue.CreateNull(),
                ["CreatedByUniqueId"] = communityEvent.CreatedByUniqueId,
                ["ModifiedByUniqueId"] = communityEvent.ModifiedByUniqueId,
                ["CreatedUtc"] = JToken.FromObject(communityEvent.CreatedUtc),
                ["ModifiedUtc"] = JToken.FromObject(communityEvent.ModifiedUtc),
                ["Signups"] = JsonConvert.SerializeObject(communityEvent.Signups ?? new List<EventSignup>()),
            };
        }

        internal static EmailJob ToEmailJob(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new EmailJob
            {
                Id = ParseLegacyGuid(rawId),
                Status = Enum.TryParse<EmailJobStatus>(doc.Value<string>("Status"), out var status)
                    ? status
                    : EmailJobStatus.Queued,
                Category = doc.Value<string>("Category"),
                FromEmail = doc.Value<string>("FromEmail"),
                FromDisplay = doc.Value<string>("FromDisplay"),
                ToDisplay = doc.Value<string>("ToDisplay"),
                OriginalSenderEmail = doc.Value<string>("OriginalSenderEmail"),
                OriginalSenderDisplay = doc.Value<string>("OriginalSenderDisplay"),
                Subject = doc.Value<string>("Subject"),
                ContentBlobPath = doc.Value<string>("ContentBlobPath"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                StartedUtc = doc["StartedUtc"]?.ToObject<DateTime?>(),
                CompletedUtc = doc["CompletedUtc"]?.ToObject<DateTime?>(),
                LastProgressUtc = doc["LastProgressUtc"]?.ToObject<DateTime?>(),
                CreatedByUserId = doc.Value<string>("CreatedByUserId"),
                CreatedByDisplayName = doc.Value<string>("CreatedByDisplayName"),
                MaxRecipientAttempts = doc.Value<int?>("MaxRecipientAttempts") ?? 0,
                TotalRecipients = doc.Value<int?>("TotalRecipients") ?? 0,
                SentCount = doc.Value<int?>("SentCount") ?? 0,
                FailedCount = doc.Value<int?>("FailedCount") ?? 0,
                SuppressedCount = doc.Value<int?>("SuppressedCount") ?? 0,
                LastError = doc.Value<string>("LastError"),
                GroupRecipients = doc.Value<bool?>("GroupRecipients") ?? false,
                InternetMessageId = doc.Value<string>("InternetMessageId"),
                ReplyToEmail = doc.Value<string>("ReplyToEmail"),
                ReplyToDisplay = doc.Value<string>("ReplyToDisplay"),
                Attachments = ToEmailJobAttachments(doc["Attachments"]),
                Recipients = ToEmailJobRecipients(doc["Recipients"]),
                // Populate ETag on every read path (per the repository convention) so query results can
                // be written back under optimistic concurrency; point reads overwrite this from the
                // response header with the same value.
                ETag = doc.Value<string>("_etag"),
            };
        }

        internal static JObject ToEmailJobDocument(EmailJob job)
        {
            var recipientsArray = new JArray();
            foreach (var r in job.Recipients ?? new List<EmailJobRecipient>())
            {
                recipientsArray.Add(
                    new JObject
                    {
                        ["Email"] = r.Email,
                        ["HomeId"] = r.HomeId.ToString("D"),
                        ["Status"] = r.Status.ToString(),
                        ["AttemptCount"] = r.AttemptCount,
                        ["LastAttemptUtc"] =
                            r.LastAttemptUtc != null ? JToken.FromObject(r.LastAttemptUtc) : JValue.CreateNull(),
                        ["Error"] = r.Error != null ? r.Error : JValue.CreateNull(),
                        ["SentUtc"] = r.SentUtc != null ? JToken.FromObject(r.SentUtc) : JValue.CreateNull(),
                        ["DeliveryStatus"] = r.DeliveryStatus.ToString(),
                        ["DeliveryStatusUpdatedUtc"] =
                            r.DeliveryStatusUpdatedUtc != null
                                ? JToken.FromObject(r.DeliveryStatusUpdatedUtc)
                                : JValue.CreateNull(),
                        ["ProviderMessageId"] = r.ProviderMessageId != null ? r.ProviderMessageId : JValue.CreateNull(),
                        ["Provider"] = r.Provider != null ? r.Provider : JValue.CreateNull(),
                        ["UnsubscribeLinkId"] =
                            r.UnsubscribeLinkId != null ? r.UnsubscribeLinkId : JValue.CreateNull(),
                        ["SuppressedUtc"] =
                            r.SuppressedUtc != null ? JToken.FromObject(r.SuppressedUtc) : JValue.CreateNull(),
                        ["SuppressionReason"] =
                            r.SuppressionReason != null ? r.SuppressionReason.ToString() : JValue.CreateNull(),
                    }
                );
            }

            return new JObject
            {
                ["id"] = ToEmailJobDocumentId(job.Id),
                ["Status"] = job.Status.ToString(),
                ["Category"] = job.Category,
                ["FromEmail"] = job.FromEmail,
                ["FromDisplay"] = job.FromDisplay,
                ["ToDisplay"] = job.ToDisplay,
                ["OriginalSenderEmail"] = job.OriginalSenderEmail,
                ["OriginalSenderDisplay"] = job.OriginalSenderDisplay,
                ["Subject"] = job.Subject,
                ["ContentBlobPath"] = job.ContentBlobPath,
                ["CreatedUtc"] = JToken.FromObject(job.CreatedUtc),
                ["StartedUtc"] = job.StartedUtc != null ? JToken.FromObject(job.StartedUtc) : JValue.CreateNull(),
                ["CompletedUtc"] = job.CompletedUtc != null ? JToken.FromObject(job.CompletedUtc) : JValue.CreateNull(),
                ["LastProgressUtc"] =
                    job.LastProgressUtc != null ? JToken.FromObject(job.LastProgressUtc) : JValue.CreateNull(),
                ["CreatedByUserId"] = job.CreatedByUserId,
                ["CreatedByDisplayName"] = job.CreatedByDisplayName,
                ["MaxRecipientAttempts"] = job.MaxRecipientAttempts,
                ["TotalRecipients"] = job.TotalRecipients,
                ["SentCount"] = job.SentCount,
                ["FailedCount"] = job.FailedCount,
                ["SuppressedCount"] = job.SuppressedCount,
                ["LastError"] = job.LastError != null ? job.LastError : JValue.CreateNull(),
                ["GroupRecipients"] = job.GroupRecipients,
                ["InternetMessageId"] = job.InternetMessageId,
                ["ReplyToEmail"] = job.ReplyToEmail,
                ["ReplyToDisplay"] = job.ReplyToDisplay,
                ["Attachments"] = new JArray(
                    (job.Attachments ?? new List<EmailJobAttachment>()).Select(a => new JObject
                    {
                        ["FileName"] = a.FileName,
                        ["BlobPath"] = a.BlobPath,
                        ["ContentType"] = a.ContentType,
                        ["Size"] = a.Size,
                    })
                ),
                ["Recipients"] = recipientsArray,
            };
        }

        private static List<EmailJobRecipient> ToEmailJobRecipients(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new List<EmailJobRecipient>();
            }

            if (token is JArray array)
            {
                return array
                    .OfType<JObject>()
                    .Select(r => new EmailJobRecipient
                    {
                        Email = r.Value<string>("Email"),
                        HomeId = Guid.TryParse(r.Value<string>("HomeId"), out var hid) ? hid : Guid.Empty,
                        Status = Enum.TryParse<EmailJobRecipientStatus>(r.Value<string>("Status"), out var rs)
                            ? rs
                            : EmailJobRecipientStatus.Pending,
                        AttemptCount = r.Value<int?>("AttemptCount") ?? 0,
                        LastAttemptUtc = r["LastAttemptUtc"]?.ToObject<DateTime?>(),
                        Error = r.Value<string>("Error"),
                        SentUtc = r["SentUtc"]?.ToObject<DateTime?>(),
                        DeliveryStatus = Enum.TryParse<DeliveryStatus>(r.Value<string>("DeliveryStatus"), out var ds)
                            ? ds
                            : DeliveryStatus.Unknown,
                        DeliveryStatusUpdatedUtc = r["DeliveryStatusUpdatedUtc"]?.ToObject<DateTime?>(),
                        ProviderMessageId = r.Value<string>("ProviderMessageId"),
                        Provider = r.Value<string>("Provider"),
                        UnsubscribeLinkId = r.Value<string>("UnsubscribeLinkId"),
                        SuppressedUtc = r["SuppressedUtc"]?.ToObject<DateTime?>(),
                        SuppressionReason = Enum.TryParse<SuppressionReason>(
                            r.Value<string>("SuppressionReason"),
                            out var sr
                        )
                            ? sr
                            : null,
                    })
                    .ToList();
            }

            return new List<EmailJobRecipient>();
        }

        private static List<EmailJobAttachment> ToEmailJobAttachments(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return new List<EmailJobAttachment>();
            if (token is JArray array)
                return array.OfType<JObject>().Select(a => new EmailJobAttachment
                {
                    FileName = a.Value<string>("FileName"),
                    BlobPath = a.Value<string>("BlobPath"),
                    ContentType = a.Value<string>("ContentType"),
                    Size = a.Value<long?>("Size") ?? 0,
                }).ToList();
            return new List<EmailJobAttachment>();
        }

        // ── Committee ────────────────────────────────────────────────

        // ── Resident (person entity) ────────────────────────────────

        internal static string ResidentEntityDocumentId(Guid residentId) => $"Resident|{residentId:D}";

        internal static Resident ToResidentEntity(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new Resident
            {
                Id = ParseLegacyGuid(rawId),
                HomeId = Guid.TryParse(doc.Value<string>("HomeId"), out var hid) ? hid : Guid.Empty,
                GivenName = doc.Value<string>("GivenName"),
                Surname = doc.Value<string>("Surname"),
                YearOfBirth = doc.Value<int?>("YearOfBirth") ?? 0,
                CollegeName = doc.Value<string>("CollegeName"),
                ResidentType = Enum.TryParse<Resident.Type>(doc.Value<string>("ResidentType"), out var rt)
                    ? rt
                    : Resident.Type.Homeowner,
                EmailAddresses =
                    DeserializeFlexible<List<EmailAddress>>(doc["EmailAddresses"]) ?? new List<EmailAddress>(),
                PhoneNumbers = DeserializeFlexible<List<PhoneNumber>>(doc["PhoneNumbers"]) ?? new List<PhoneNumber>(),
            };
        }

        internal static JObject ToResidentEntityDocument(Resident resident)
        {
            return new JObject
            {
                ["id"] = ResidentEntityDocumentId(resident.Id),
                ["HomeId"] = resident.HomeId.ToString("D"),
                ["GivenName"] = resident.GivenName,
                ["Surname"] = resident.Surname,
                ["YearOfBirth"] = resident.YearOfBirth,
                ["CollegeName"] = resident.CollegeName,
                ["ResidentType"] = resident.ResidentType.ToString(),
                ["EmailAddresses"] = JsonConvert.SerializeObject(resident.EmailAddresses ?? new List<EmailAddress>()),
                ["PhoneNumbers"] = JsonConvert.SerializeObject(resident.PhoneNumbers ?? new List<PhoneNumber>()),
            };
        }

        // ── Committee ────────────────────────────────────────────────

        internal static Committee ToCommittee(JObject doc)
        {
            return new Committee
            {
                Id = doc.Value<string>("id"),
                CommitteeEmail = doc.Value<string>("CommitteeEmail"),
                DisplayName = doc.Value<string>("DisplayName"),
                Description = doc.Value<string>("Description"),
                DisplayOrder = doc.Value<int?>("DisplayOrder") ?? 0,
                Members = ToCommitteeMembers(doc["Members"]),
                ManagementRole = Enum.TryParse<User.Role>(doc.Value<string>("ManagementRole"), out var mr) ? mr : null,
                GraphMessageRuleId = doc.Value<string>("GraphMessageRuleId"),
                LastSyncedUtc = doc.Value<DateTime?>("LastSyncedUtc"),
                LastSyncStatus = doc.Value<string>("LastSyncStatus"),
                LastSyncError = doc.Value<string>("LastSyncError"),
                ForwardingEnabled = doc.Value<bool?>("ForwardingEnabled") ?? false,
                ForwardingSenderFilter = Enum.TryParse<ForwardingSenderFilter>(
                    doc.Value<string>("ForwardingSenderFilter"), ignoreCase: true, out var fsf)
                    ? fsf
                    : ForwardingSenderFilter.DirectoryOnly,
                LastPollUtc = doc["LastPollUtc"]?.ToObject<DateTime?>(),
                LastPollStatus = doc.Value<string>("LastPollStatus"),
                LastPollError = doc.Value<string>("LastPollError"),
            };
        }

        internal static JObject ToCommitteeDocument(Committee committee)
        {
            return new JObject
            {
                ["id"] = committee.Id,
                ["CommitteeEmail"] = committee.CommitteeEmail,
                ["DisplayName"] = committee.DisplayName,
                ["Description"] = committee.Description,
                ["DisplayOrder"] = committee.DisplayOrder,
                ["Members"] = JArray.FromObject(committee.Members ?? new List<CommitteeMember>()),
                ["ManagementRole"] = committee.ManagementRole?.ToString(),
                ["GraphMessageRuleId"] = committee.GraphMessageRuleId,
                ["LastSyncedUtc"] = committee.LastSyncedUtc,
                ["LastSyncStatus"] = committee.LastSyncStatus,
                ["LastSyncError"] = committee.LastSyncError,
                ["ForwardingEnabled"] = committee.ForwardingEnabled,
                ["ForwardingSenderFilter"] = committee.ForwardingSenderFilter.ToString(),
                ["LastPollUtc"] = committee.LastPollUtc != null
                    ? JToken.FromObject(committee.LastPollUtc)
                    : JValue.CreateNull(),
                ["LastPollStatus"] = committee.LastPollStatus,
                ["LastPollError"] = committee.LastPollError,
            };
        }

        private static List<CommitteeMember> ToCommitteeMembers(JToken token)
        {
            if (token is JArray arr)
            {
                return arr.Select(t => new CommitteeMember
                    {
                        Id = Guid.TryParse(t["Id"]?.ToString(), out var id) ? id : Guid.Empty,
                        ResidentId = Guid.TryParse(t["ResidentId"]?.ToString(), out var rid) ? rid : Guid.Empty,
                        Title = t.Value<string>("Title"),
                        Bio = t.Value<string>("Bio"),
                        PhotoBlobPath = t.Value<string>("PhotoBlobPath"),
                        PhotoContentType = t.Value<string>("PhotoContentType"),
                        ReceivesForwardedEmail = t.Value<bool?>("ReceivesForwardedEmail") ?? false,
                        PhotoOffsetY = t.Value<int?>("PhotoOffsetY") ?? 50,
                        DisplayOrder = t.Value<int?>("DisplayOrder") ?? 0,
                    })
                    .ToList();
            }

            return new List<CommitteeMember>();
        }

        // ── HeldMessage ─────────────────────────────────────────────────

        internal static string ToHeldMessageDocumentId(Guid id) => $"HeldMessage|{id:D}";

        internal static HeldMessage ToHeldMessage(JObject doc)
        {
            var rawId = doc.Value<string>("id");
            return new HeldMessage
            {
                Id = ParseLegacyGuid(rawId),
                CommitteeId = doc.Value<string>("CommitteeId"),
                CommitteeEmail = doc.Value<string>("CommitteeEmail"),
                InternetMessageId = doc.Value<string>("InternetMessageId"),
                SenderEmail = doc.Value<string>("SenderEmail"),
                SenderName = doc.Value<string>("SenderName"),
                Subject = doc.Value<string>("Subject"),
                ReceivedUtc = doc["ReceivedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                HeldUtc = doc["HeldUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                NotifiedUtc = doc["NotifiedUtc"]?.ToObject<DateTime?>(),
                // TryParseDefined (not Enum.TryParse) rejects out-of-range numerics (e.g. "999"), which
                // would otherwise round-trip back to storage as an undefined status and render as an
                // unknown state in any status-keyed UI/switch. A corrupt/legacy value falls back to Held -
                // consistent with the SpamVerdict/SpamConfidence parses on the same object.
                Status = EnumParse.TryParseDefined<HeldMessageStatus>(doc.Value<string>("Status"), out var s)
                    ? s
                    : HeldMessageStatus.Held,
                // TryParseDefined rejects out-of-range numerics (e.g. "999"): an undefined SpamConfidence
                // would otherwise spuriously satisfy the sweep's >= threshold check. A corrupt/legacy
                // stored value safely falls back to Unknown.
                SpamVerdict = EnumParse.TryParseDefined<SpamVerdict>(doc.Value<string>("SpamVerdict"), out var sv)
                    ? sv
                    : SpamVerdict.Unknown,
                SpamConfidence = EnumParse.TryParseDefined<SpamConfidence>(doc.Value<string>("SpamConfidence"), out var sc)
                    ? sc
                    : SpamConfidence.Unknown,
                SpamReason = doc.Value<string>("SpamReason"),
                ReviewedByUserId = doc.Value<string>("ReviewedByUserId"),
                ReviewedUtc = doc["ReviewedUtc"]?.ToObject<DateTime?>(),
                // Populate ETag on every read path (per the repository convention) so query results can
                // be written back under optimistic concurrency; point reads overwrite this from the
                // response header with the same value.
                ETag = doc.Value<string>("_etag"),
            };
        }

        internal static JObject ToHeldMessageDocument(HeldMessage msg)
        {
            return new JObject
            {
                ["id"] = ToHeldMessageDocumentId(msg.Id),
                ["CommitteeId"] = msg.CommitteeId,
                ["CommitteeEmail"] = msg.CommitteeEmail,
                ["InternetMessageId"] = msg.InternetMessageId,
                ["SenderEmail"] = msg.SenderEmail,
                ["SenderName"] = msg.SenderName,
                ["Subject"] = msg.Subject,
                ["ReceivedUtc"] = JToken.FromObject(msg.ReceivedUtc),
                ["HeldUtc"] = JToken.FromObject(msg.HeldUtc),
                ["NotifiedUtc"] = msg.NotifiedUtc != null
                    ? JToken.FromObject(msg.NotifiedUtc)
                    : JValue.CreateNull(),
                ["Status"] = msg.Status.ToString(),
                ["SpamVerdict"] = msg.SpamVerdict.ToString(),
                ["SpamConfidence"] = msg.SpamConfidence.ToString(),
                ["SpamReason"] = msg.SpamReason,
                ["ReviewedByUserId"] = msg.ReviewedByUserId,
                ["ReviewedUtc"] = msg.ReviewedUtc != null
                    ? JToken.FromObject(msg.ReviewedUtc)
                    : JValue.CreateNull(),
            };
        }
    }
}
