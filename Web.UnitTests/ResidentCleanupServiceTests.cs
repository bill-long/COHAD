using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public class ResidentCleanupServiceTests
{
    private static readonly Guid ResidentA = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid ResidentB = Guid.Parse("aaaa0000-0000-0000-0000-000000000002");
    private static readonly Guid ResidentC = Guid.Parse("aaaa0000-0000-0000-0000-000000000003");

    private readonly Mock<ICommitteeRepository> _committeeRepo = new();
    private readonly Mock<IDocumentFileStore> _fileStore = new();
    private readonly Mock<IUserRepository> _userRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly CommitteeListCache _listCache;
    private readonly ResidentCleanupService _service;

    public ResidentCleanupServiceTests()
    {
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User>());
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);
        _listCache = new CommitteeListCache(_committeeRepo.Object, new MemoryCache(new MemoryCacheOptions()));
        _service = new ResidentCleanupService(
            _committeeRepo.Object,
            _listCache,
            _fileStore.Object,
            _userRepo.Object,
            _auditRepo.Object,
            Mock.Of<ILogger<ResidentCleanupService>>()
        );
    }

    [Fact]
    public async Task EmptyList_NoOp()
    {
        await _service.HandleDeletedResidentsAsync(Array.Empty<Guid>());

        _committeeRepo.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task Null_NoOp()
    {
        await _service.HandleDeletedResidentsAsync(null!);

        _committeeRepo.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task RemovesMemberFromOneCommittee()
    {
        var committee = MakeCommittee("board", (ResidentA, null), (ResidentB, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Single(committee.Members);
        Assert.Equal(ResidentB, committee.Members[0].ResidentId);
        _committeeRepo.Verify(r => r.UpsertAsync(committee), Times.Once);
    }

    [Fact]
    public async Task RemovesMemberFromMultipleCommittees()
    {
        var board = MakeCommittee("board", (ResidentA, null), (ResidentB, null));
        var welcome = MakeCommittee("welcome", (ResidentA, null));
        var garden = MakeCommittee("garden", (ResidentC, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { board, welcome, garden });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Single(board.Members);
        Assert.Empty(welcome.Members);
        Assert.Single(garden.Members);
        _committeeRepo.Verify(r => r.UpsertAsync(board), Times.Once);
        _committeeRepo.Verify(r => r.UpsertAsync(welcome), Times.Once);
        _committeeRepo.Verify(r => r.UpsertAsync(garden), Times.Never);
    }

    [Fact]
    public async Task DeletesPhotoBlobForRemovedMember()
    {
        var committee = MakeCommittee("board", (ResidentA, "committees/board/photo-aaa.webp"), (ResidentB, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        _fileStore.Verify(f => f.DeleteAsync("committees/board/photo-aaa.webp"), Times.Once);
    }

    [Fact]
    public async Task PhotoDeleteFailureDoesNotPreventMemberRemoval()
    {
        var committee = MakeCommittee("board", (ResidentA, "bad-blob-path"));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);
        _fileStore
            .Setup(f => f.DeleteAsync("bad-blob-path"))
            .ThrowsAsync(new InvalidOperationException("Storage error"));

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Empty(committee.Members);
        _committeeRepo.Verify(r => r.UpsertAsync(committee), Times.Once);
    }

    [Fact]
    public async Task NoMatchingMembers_DoesNotUpsert()
    {
        var committee = MakeCommittee("board", (ResidentB, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Single(committee.Members);
        _committeeRepo.Verify(r => r.UpsertAsync(It.IsAny<Committee>()), Times.Never);
    }

    [Fact]
    public async Task MultipleResidentsRemovedAtOnce()
    {
        var committee = MakeCommittee("board", (ResidentA, null), (ResidentB, null), (ResidentC, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA, ResidentC });

        Assert.Single(committee.Members);
        Assert.Equal(ResidentB, committee.Members[0].ResidentId);
    }

    // ── User resident-link clearing ─────────────────────────────────────

    [Fact]
    public async Task ClearsUserLinksPointingAtDeletedResidents()
    {
        var linked = new User { UniqueId = "u1", Emails = "u1@test.com", ResidentId = ResidentA };
        var linkedElsewhere = new User { UniqueId = "u2", ResidentId = ResidentB };
        var unlinked = new User { UniqueId = "u3" };
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { linked, linkedElsewhere, unlinked });
        _userRepo.Setup(r => r.GetByUniqueIdAsync("u1")).ReturnsAsync(linked);
        _userRepo.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Null(linked.ResidentId);
        Assert.Equal(ResidentB, linkedElsewhere.ResidentId);
        _userRepo.Verify(r => r.UpsertAsync(linked), Times.Once);
        _userRepo.Verify(r => r.UpsertAsync(linkedElsewhere), Times.Never);
        _userRepo.Verify(r => r.UpsertAsync(unlinked), Times.Never);
        _auditRepo.Verify(
            a => a.AddAsync(It.Is<NewAuditLogEntry>(e =>
                e.SubjectId == "u1"
                && e.UserId == "system"
                && e.Action.Contains("resident link")
            )),
            Times.Once
        );
    }

    [Fact]
    public async Task UpsertsFreshReRead_NotTheStaleListSnapshot()
    {
        // The list snapshot is stale: by upsert time the user has gained a role. The cascade must
        // write the fresh document so the concurrent change is not reverted.
        var stale = new User { UniqueId = "u1", ResidentId = ResidentA, Roles = new List<User.Role>() };
        var fresh = new User
        {
            UniqueId = "u1",
            ResidentId = ResidentA,
            Roles = new List<User.Role> { User.Role.Administrator },
        };
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { stale });
        _userRepo.Setup(r => r.GetByUniqueIdAsync("u1")).ReturnsAsync(fresh);
        User? upserted = null;
        _userRepo.Setup(r => r.UpsertAsync(It.IsAny<User>())).Callback<User>(u => upserted = u).ReturnsAsync((User u) => u);
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Same(fresh, upserted);
        Assert.Null(fresh.ResidentId);
        Assert.Contains(User.Role.Administrator, upserted!.Roles);
    }

    [Fact]
    public async Task SkipsUserWhoseFreshLinkAlreadyChanged()
    {
        // Between the snapshot and the write, someone already cleared or re-pointed the link.
        var stale = new User { UniqueId = "u1", ResidentId = ResidentA };
        var fresh = new User { UniqueId = "u1", ResidentId = ResidentC };
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { stale });
        _userRepo.Setup(r => r.GetByUniqueIdAsync("u1")).ReturnsAsync(fresh);
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        _userRepo.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
        Assert.Equal(ResidentC, fresh.ResidentId);
    }

    [Fact]
    public async Task AuditFailureAfterAppliedClearDoesNotUndoOrMisreportIt()
    {
        // The clear has been applied when the audit write runs; a failed audit must not be reported
        // as a failed clear, and must not stop the sweep.
        var linked = new User { UniqueId = "u1", Emails = "u1@test.com", ResidentId = ResidentA };
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { linked });
        _userRepo.Setup(r => r.GetByUniqueIdAsync("u1")).ReturnsAsync(linked);
        _userRepo.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
        _auditRepo
            .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .ThrowsAsync(new InvalidOperationException("audit store down"));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Null(linked.ResidentId);
        _userRepo.Verify(r => r.UpsertAsync(linked), Times.Once);
    }

    [Fact]
    public async Task UserLinkClearFailureDoesNotStopOtherUsers()
    {
        var failing = new User { UniqueId = "u1", ResidentId = ResidentA };
        var succeeding = new User { UniqueId = "u2", ResidentId = ResidentA };
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { failing, succeeding });
        _userRepo.Setup(r => r.GetByUniqueIdAsync("u1")).ReturnsAsync(failing);
        _userRepo.Setup(r => r.GetByUniqueIdAsync("u2")).ReturnsAsync(succeeding);
        _userRepo.Setup(r => r.UpsertAsync(failing)).ThrowsAsync(new InvalidOperationException("Cosmos error"));
        _userRepo.Setup(r => r.UpsertAsync(succeeding)).ReturnsAsync((User u) => u);
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Null(succeeding.ResidentId);
        _userRepo.Verify(r => r.UpsertAsync(succeeding), Times.Once);
    }

    [Fact]
    public async Task UserListReadFailureStillCompletesCommitteeCleanup()
    {
        var committee = MakeCommittee("board", (ResidentA, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);
        _userRepo.Setup(r => r.GetAllAsync()).ThrowsAsync(new InvalidOperationException("Cosmos error"));

        await _service.HandleDeletedResidentsAsync(new[] { ResidentA });

        Assert.Empty(committee.Members);
        _committeeRepo.Verify(r => r.UpsertAsync(committee), Times.Once);
    }

    private static Committee MakeCommittee(string id, params (Guid residentId, string? photoBlobPath)[] members) =>
        new()
        {
            Id = id,
            DisplayName = id,
            Members = members
                .Select(
                    (m, i) =>
                        new CommitteeMember
                        {
                            Id = Guid.NewGuid(),
                            ResidentId = m.residentId,
                            PhotoBlobPath = m.photoBlobPath,
                            DisplayOrder = i,
                        }
                )
                .ToList(),
        };
}
