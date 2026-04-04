using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockCommitteeRepository : ICommitteeRepository
    {
        private readonly Dictionary<string, Committee> _committees = new(StringComparer.OrdinalIgnoreCase);

        public MockCommitteeRepository()
        {
            Seed();
        }

        public Task<List<Committee>> GetAllAsync()
        {
            lock (_committees)
            {
                return Task.FromResult(
                    _committees.Values.OrderBy(c => c.DisplayOrder).Select(Clone).ToList());
            }
        }

        public Task<Committee> GetByIdAsync(string committeeKey)
        {
            lock (_committees)
            {
                return Task.FromResult(
                    _committees.TryGetValue(committeeKey, out var c) ? Clone(c) : null);
            }
        }

        public Task<Committee> UpsertAsync(Committee committee)
        {
            lock (_committees)
            {
                _committees[committee.Id] = Clone(committee);
            }

            return Task.FromResult(committee);
        }

        private void Seed()
        {
            var committees = new List<Committee>
            {
                new Committee
                {
                    Id = "board",
                    CommitteeEmail = "board@cohad.org",
                    DisplayName = "Board",
                    Description = "Oversees COHAD operations, finances, and community standards.",
                    DisplayOrder = 1,
                    ManagementRole = User.Role.Board,
                    Members = new List<CommitteeMember>
                    {
                        new CommitteeMember
                        {
                            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                            DisplayName = "Mock Resident",
                            Title = "President",
                            Bio = "Longtime Canyon Oaks resident and community volunteer.",
                            Email = "mock@cohad.local",
                            ReceivesForwardedEmail = true,
                            DisplayOrder = 1
                        },
                        new CommitteeMember
                        {
                            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                            DisplayName = "Taylor Doe",
                            Title = "Treasurer",
                            Bio = "Manages the HOA budget and financial reports.",
                            Email = "taylor@cohad.local",
                            ReceivesForwardedEmail = true,
                            DisplayOrder = 2
                        }
                    }
                },
                new Committee
                {
                    Id = "welcome",
                    CommitteeEmail = "welcome@cohad.org",
                    DisplayName = "Welcome Committee",
                    Description = "Greets new residents and helps them settle into the neighborhood.",
                    DisplayOrder = 2,
                    ManagementRole = User.Role.WelcomeCommittee,
                    Members = new List<CommitteeMember>
                    {
                        new CommitteeMember
                        {
                            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                            DisplayName = "Mock Resident",
                            Bio = "Enjoys meeting new neighbors and organizing welcome events.",
                            Email = "mock@cohad.local",
                            ReceivesForwardedEmail = true,
                            DisplayOrder = 1
                        }
                    }
                },
                new Committee
                {
                    Id = "garden",
                    CommitteeEmail = "gardenclub@cohad.org",
                    DisplayName = "Garden Club",
                    Description = "Organizes neighborhood beautification, garden tours, and plant swaps.",
                    DisplayOrder = 3,
                    ManagementRole = User.Role.GardenClub,
                    Members = new List<CommitteeMember>()
                },
                new Committee
                {
                    Id = "social",
                    CommitteeEmail = "social@cohad.org",
                    DisplayName = "Social Committee",
                    Description = "Plans community events, holiday celebrations, and neighborhood gatherings.",
                    DisplayOrder = 4,
                    ManagementRole = User.Role.SocialCommittee,
                    Members = new List<CommitteeMember>
                    {
                        new CommitteeMember
                        {
                            Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                            DisplayName = "Taylor Doe",
                            Title = "Chair",
                            Email = "taylor@cohad.local",
                            ReceivesForwardedEmail = true,
                            DisplayOrder = 1
                        }
                    }
                },
                new Committee
                {
                    Id = "sunshine",
                    CommitteeEmail = "sunshine@cohad.org",
                    DisplayName = "Sunshine Committee",
                    Description = "Sends cards and well-wishes to neighbors during milestones and difficult times.",
                    DisplayOrder = 5,
                    ManagementRole = User.Role.SunshineCommittee,
                    Members = new List<CommitteeMember>()
                },
                new Committee
                {
                    Id = "architectural",
                    CommitteeEmail = "ac@cohad.org",
                    DisplayName = "Architectural Committee",
                    Description = "Reviews and approves plans for new structures and modifications to ensure compliance with community covenants.",
                    DisplayOrder = 6,
                    ManagementRole = User.Role.ArchitecturalCommittee,
                    Members = new List<CommitteeMember>
                    {
                        new CommitteeMember
                        {
                            Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                            DisplayName = "Mock Resident",
                            Title = "Chair",
                            Bio = "Helps maintain the architectural standards of Canyon Oaks.",
                            Email = "mock@cohad.local",
                            ReceivesForwardedEmail = true,
                            DisplayOrder = 1
                        }
                    }
                }
            };

            foreach (var c in committees)
                _committees[c.Id] = c;
        }

        private static Committee Clone(Committee c) => new()
        {
            Id = c.Id,
            CommitteeEmail = c.CommitteeEmail,
            DisplayName = c.DisplayName,
            Description = c.Description,
            DisplayOrder = c.DisplayOrder,
            Members = c.Members?.Select(CloneMember).ToList() ?? new List<CommitteeMember>(),
            ManagementRole = c.ManagementRole,
            GraphMessageRuleId = c.GraphMessageRuleId,
            LastSyncedUtc = c.LastSyncedUtc,
            LastSyncStatus = c.LastSyncStatus,
            LastSyncError = c.LastSyncError
        };

        private static CommitteeMember CloneMember(CommitteeMember m) => new()
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Title = m.Title,
            Bio = m.Bio,
            PhotoBlobPath = m.PhotoBlobPath,
            PhotoContentType = m.PhotoContentType,
            Email = m.Email,
            ReceivesForwardedEmail = m.ReceivesForwardedEmail,
            PhotoOffsetY = m.PhotoOffsetY,
            DisplayOrder = m.DisplayOrder
        };
    }
}
