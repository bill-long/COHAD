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

        // Stable IDs for seeded residents (person entities).
        public static readonly Guid MockResident1Id = Guid.Parse("44444444-4444-4444-4444-444444444401");
        public static readonly Guid TaylorResident1Id = Guid.Parse("44444444-4444-4444-4444-444444444402");

        /// <summary>Stable IDs for seeded mock email jobs (Manage → Email list / detail testing).</summary>
        public static readonly Guid SampleEmailJobSeed1 = Guid.Parse("33333333-3333-3333-3333-333333333301");
        public static readonly Guid SampleEmailJobSeed2 = Guid.Parse("33333333-3333-3333-3333-333333333302");
        public static readonly Guid SampleEmailJobSeed3 = Guid.Parse("33333333-3333-3333-3333-333333333303");
        public static readonly Guid SampleEmailJobSeed4 = Guid.Parse("33333333-3333-3333-3333-333333333304");
        public static readonly Guid SampleEmailJobSeed5 = Guid.Parse("33333333-3333-3333-3333-333333333305");
        public static readonly Guid SampleEmailJobSeed6 = Guid.Parse("33333333-3333-3333-3333-333333333306");
        public static readonly Guid SampleEmailJobSeed7 = Guid.Parse("33333333-3333-3333-3333-333333333307");
        public static readonly Guid SampleEmailJobSeed8 = Guid.Parse("33333333-3333-3333-3333-333333333308");
    }
}
