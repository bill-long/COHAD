using System.Text.Json.Serialization;

namespace Web.Models
{
    /// <summary>
    /// The outcome of running a held (non-directory) message through the LLM spam classifier.
    /// <see cref="Unknown"/> means the message was never classified or the classifier failed — it is the
    /// fail-safe value that keeps a message on the normal moderator-notification path.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SpamVerdict
    {
        Unknown = 0,
        NotSpam = 1,
        Spam = 2,
    }

    /// <summary>
    /// How confident the classifier was in its <see cref="SpamVerdict"/>. Ordered so that a configured
    /// auto-reject threshold can be compared with <c>&gt;=</c> (e.g. threshold High rejects only High).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SpamConfidence
    {
        Unknown = 0,
        Low = 1,
        Medium = 2,
        High = 3,
    }
}
