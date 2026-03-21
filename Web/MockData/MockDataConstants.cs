using System;

namespace Web.MockData
{
    /// <summary>
    /// Fixed identities and entities for local/agent testing without Cosmos or Azure AD B2C.
    /// </summary>
    public static class MockDataConstants
    {
        public const string IdentityProvider = "cohad.mock-";

        public const string AdminNameIdentifier = "user-1";
        public static readonly string AdminUniqueId = IdentityProvider + AdminNameIdentifier;

        public static readonly Guid SampleHomeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid SecondSampleHomeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        // Back-compat aliases used by existing mock auth/user seeding.
        public const string NameIdentifier = AdminNameIdentifier;
        public static readonly string UniqueId = AdminUniqueId;

        public const string SecondaryUserNameIdentifier = "user-2";
        public static readonly string SecondaryUserUniqueId = IdentityProvider + SecondaryUserNameIdentifier;
    }
}
