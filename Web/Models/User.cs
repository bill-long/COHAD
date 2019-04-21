using System;
using System.Collections.Generic;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;

namespace Web.Models
{
    /// <summary>
    /// User
    ///
    /// For storing this in Azure Tables, we are using a partitioning scheme
    /// where every user has a unique PartitionKey (the Id), and a blank RowKey.
    /// This should make retrieval of individual users very fast when we know
    /// the Id we're looking for, which is the common case. In the rare case
    /// where we need to do something to all users, it will be slow, as we have
    /// to traverse partitions.
    /// </summary>
    public class User : TableEntity
    {
        public User()
        {
            RowKey = "";
        }

        public string Id { get => PartitionKey; set => PartitionKey = value; }
        public string CustomerId { get; set; }
        public string GivenName { get; set; }
        public string Surname { get; set; }
        public string IdentityProvider { get; set; }
        public string Emails { get; set; }
        public string StreetAddress { get; set; }

        [IgnoreProperty]
        public List<string> ChargeIds { get; set; }

        [IgnoreProperty]
        public List<string> SubscriptionIds { get; set; }

        [IgnoreProperty]
        public Roles Role { get; set; }

        [IgnoreProperty]
        public PromotionStates PromotionState { get; set; }

        public enum Roles
        {
            None,
            Member,
            Administrator
        }

        /// <summary>
        /// After a user logs in for the first time, they will have a role of
        /// None. They can request promotion in order to ask an Admin to grant
        /// them the Member role, and Admins will be notified of this request.
        /// However, we don't want to let a user repeatedly request promotion,
        /// if they have already been denied.
        /// </summary>
        public enum PromotionStates
        {
            None,       // Either the user has not requested, or they were approved and the state was cleared.
            Requested,  // Currently awaiting approval.
            Denied      // The user was denied and is now blocked from requesting promotion.
        }

        public override void ReadEntity(IDictionary<string, EntityProperty> properties, OperationContext operationContext)
        {
            base.ReadEntity(properties, operationContext);
            if (properties.ContainsKey(nameof(ChargeIds)))
            {
                ChargeIds = JsonConvert.DeserializeObject<List<string>>(properties[nameof(ChargeIds)].StringValue);
            }

            if (properties.ContainsKey(nameof(SubscriptionIds)))
            {
                SubscriptionIds =
                    JsonConvert.DeserializeObject<List<string>>(properties[nameof(SubscriptionIds)].StringValue);
            }

            if (properties.ContainsKey(nameof(Role)))
            {
                Role = Enum.Parse<Roles>(properties[nameof(Role)].StringValue);
            }

            if (properties.ContainsKey(nameof(PromotionState)))
            {
                PromotionState = Enum.Parse<PromotionStates>(properties[nameof(PromotionState)].StringValue);
            }
        }

        public override IDictionary<string, EntityProperty> WriteEntity(OperationContext operationContext)
        {
            var e = base.WriteEntity(operationContext);
            if (ChargeIds != null)
            {
                e[nameof(ChargeIds)] = new EntityProperty(JsonConvert.SerializeObject(ChargeIds));
            }

            if (SubscriptionIds != null)
            {
                e[nameof(SubscriptionIds)] = new EntityProperty(JsonConvert.SerializeObject(SubscriptionIds));
            }

            e[nameof(Role)] = new EntityProperty(Role.ToString());
            e[nameof(PromotionState)] = new EntityProperty(PromotionState.ToString());

            return e;
        }
    }
}
