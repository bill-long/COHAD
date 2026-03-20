using System;

namespace Web.MockData
{
    /// <summary>
    /// Fixed identities and entities for local/agent testing without Cosmos or Azure AD B2C.
    /// </summary>
    public static class MockDataConstants
    {
        public const string IdentityProvider = "https://cohad.mock/";
        public const string NameIdentifier = "user-1";
        public static readonly string UniqueId = IdentityProvider + NameIdentifier;

        public static readonly Guid SampleHomeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    }
}
