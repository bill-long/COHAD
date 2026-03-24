using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Web.Models;

namespace Web.UpdateModels
{
    public class YouthServiceUpsertRequest
    {
        public Guid? Id { get; set; }

        public string Name { get; set; }

        public List<string> Services { get; set; } = new List<string>();

        public int? BornYear { get; set; }

        public string Phone { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public PreferredContactMethod ContactMethod { get; set; } = PreferredContactMethod.Text;

        public string Email { get; set; }

        public string Address { get; set; }

        public string ParentNote { get; set; }
    }
}
