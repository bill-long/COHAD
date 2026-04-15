#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Web.Models;

namespace Web.Services.Repositories
{
    public interface IEmailDeliveryEventRepository
    {
        /// <summary>
        /// Stores a delivery event. Uses idempotent upsert semantics — if an event with the same ID
        /// already exists, it is merged with the stored event rather than fully overwritten, preserving
        /// existing values except for fields that the repository implementation explicitly updates.
        /// </summary>
        Task AddAsync(EmailDeliveryEvent deliveryEvent);

        /// <summary>
        /// Returns all delivery events for the given job, ordered by ReceivedUtc.
        /// </summary>
        Task<List<EmailDeliveryEvent>> GetByJobIdAsync(Guid jobId);

        /// <summary>
        /// Deletes all delivery events for the given job (cleanup after job retention expires).
        /// </summary>
        Task DeleteByJobIdAsync(Guid jobId);
    }
}
