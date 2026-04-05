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
    private readonly CommitteeListCache _listCache;
    private readonly ResidentCleanupService _service;

    public ResidentCleanupServiceTests()
    {
        _listCache = new CommitteeListCache(_committeeRepo.Object, new MemoryCache(new MemoryCacheOptions()));
        _service = new ResidentCleanupService(
            _committeeRepo.Object,
            _listCache,
            _fileStore.Object,
            Mock.Of<ILogger<ResidentCleanupService>>()
        );
    }

    [Fact]
    public async Task EmptyList_NoOp()
    {
        await _service.RemoveFromCommitteesAsync(Array.Empty<Guid>());

        _committeeRepo.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task Null_NoOp()
    {
        await _service.RemoveFromCommitteesAsync(null!);

        _committeeRepo.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task RemovesMemberFromOneCommittee()
    {
        var committee = MakeCommittee("board", (ResidentA, null), (ResidentB, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        await _service.RemoveFromCommitteesAsync(new[] { ResidentA });

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

        await _service.RemoveFromCommitteesAsync(new[] { ResidentA });

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

        await _service.RemoveFromCommitteesAsync(new[] { ResidentA });

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

        await _service.RemoveFromCommitteesAsync(new[] { ResidentA });

        Assert.Empty(committee.Members);
        _committeeRepo.Verify(r => r.UpsertAsync(committee), Times.Once);
    }

    [Fact]
    public async Task NoMatchingMembers_DoesNotUpsert()
    {
        var committee = MakeCommittee("board", (ResidentB, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        await _service.RemoveFromCommitteesAsync(new[] { ResidentA });

        Assert.Single(committee.Members);
        _committeeRepo.Verify(r => r.UpsertAsync(It.IsAny<Committee>()), Times.Never);
    }

    [Fact]
    public async Task MultipleResidentsRemovedAtOnce()
    {
        var committee = MakeCommittee("board", (ResidentA, null), (ResidentB, null), (ResidentC, null));
        _committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        _committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        await _service.RemoveFromCommitteesAsync(new[] { ResidentA, ResidentC });

        Assert.Single(committee.Members);
        Assert.Equal(ResidentB, committee.Members[0].ResidentId);
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
