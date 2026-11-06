using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;
using Xunit;

namespace MioKit.LockBreaker.Tests;

public sealed class FileLockQueryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MioKit.LockBreaker.Tests", Guid.NewGuid().ToString("N"));
    private readonly string first;
    private readonly string second;
    private readonly string folder;

    public FileLockQueryTests()
    {
        Directory.CreateDirectory(root);
        first = Path.Combine(root, "first.txt");
        second = Path.Combine(root, "second.txt");
        folder = Path.Combine(root, "folder");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        Directory.CreateDirectory(folder);
    }

    [Fact]
    public void ScansHandlesOnceAndKeepsEachRestartManagerFilesAttribution()
    {
        var handleCalls = 0;
        var rmCalls = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var result = FileLockQuery.FindLockOwners(
            [first, second, folder, first.ToUpperInvariant()], cancellation.Token,
            (targets, token) =>
            {
                handleCalls++;
                Assert.Equal(cancellation.Token, token);
                Assert.Equal(3, targets.Count);
                Assert.Single(targets, target => target.Path == folder && target.IsDirectory);
                return new HandleScanResult(
                    [new(new LockingProcess { ProcessId = 101, ProcessName = "first-owner" }, [first, folder])],
                    targets.Select(target => target.Path).ToArray(), []);
            },
            (path, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                rmCalls.Add(path);
                return path == first
                    ? new RestartManagerResult([new LockingProcess
                    {
                        ProcessId = 101, ProcessName = "first-owner", ServiceName = "test-service"
                    }])
                    : new RestartManagerResult([new LockingProcess { ProcessId = 202, ProcessName = "second-owner" }]);
            });

        Assert.Equal(1, handleCalls);
        Assert.Equal(new[] { first, second }, rmCalls);
        Assert.Equal(3, result.TotalTargetCount);
        Assert.Equal(3, result.CompletedTargetCount);
        Assert.Empty(result.Diagnostics);
        var firstOwner = Assert.IsType<FileLockOwnerItem>(Assert.Single(result.Owners, owner => owner.ProcessId == 101));
        Assert.Equal(new[] { first, folder }.Order(StringComparer.OrdinalIgnoreCase), firstOwner.TargetPaths);
        Assert.Equal("test-service", firstOwner.ServiceName);
        var secondOwner = Assert.IsType<FileLockOwnerItem>(Assert.Single(result.Owners, owner => owner.ProcessId == 202));
        Assert.Equal(new[] { second }, secondOwner.TargetPaths);
    }

    [Fact]
    public void PreservesValidTargetsWhenAnotherPathIsMissing()
    {
        var missing = Path.Combine(root, "missing.txt");
        var scanCalls = 0;
        var result = FileLockQuery.FindLockOwners([missing, first], CancellationToken.None,
            (targets, _) =>
            {
                scanCalls++;
                Assert.Equal(first, Assert.Single(targets).Path);
                return new([], [first], []);
            },
            (path, _) =>
            {
                Assert.Equal(first, path);
                return new([new LockingProcess { ProcessId = 101, ProcessName = "owner" }]);
            });

        Assert.Equal(1, scanCalls);
        Assert.Equal(2, result.TotalTargetCount);
        Assert.Equal(1, result.CompletedTargetCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == LockDiagnosticKind.TargetFailure
            && diagnostic.TargetPath == missing && diagnostic.Stage == "GetFileAttributes");
        Assert.True(result.HasFailures);
        Assert.Equal(new[] { first }, Assert.IsType<FileLockOwnerItem>(Assert.Single(result.Owners)).TargetPaths);
    }

    [Fact]
    public void DoesNotCallEitherDetectorWhenAllTargetsAreInvalid()
    {
        var result = FileLockQuery.FindLockOwners([Path.Combine(root, "missing.txt"), "bad\0path"],
            CancellationToken.None,
            (_, _) => throw new InvalidOperationException("Handle scan must not run"),
            (_, _) => throw new InvalidOperationException("Restart Manager must not run"));

        Assert.Empty(result.Owners);
        Assert.Equal(2, result.TotalTargetCount);
        Assert.Equal(0, result.CompletedTargetCount);
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact]
    public void KeepsDetectedOwnersAndBothDetectorDiagnosticsOnPartialFailure()
    {
        var result = FileLockQuery.FindLockOwners([first, folder], CancellationToken.None,
            (_, _) => new(
                [new(new LockingProcess { ProcessId = 101, ProcessName = "owner" }, [folder])],
                [folder], [new(LockDiagnosticKind.EngineFailure, "HandleScanFailed", "handle scan incomplete")]),
            (_, _) => new([], [new(LockDiagnosticKind.EngineFailure, "RestartManagerFailed", "restart manager unavailable")]));

        Assert.Equal(new[] { folder }, Assert.IsType<FileLockOwnerItem>(Assert.Single(result.Owners)).TargetPaths);
        Assert.Equal(1, result.CompletedTargetCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message == "handle scan incomplete");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("restart manager unavailable"));
        Assert.True(result.HasFailures);
    }

    [Fact]
    public void RestartManagerStillRunsWhenTheHandleDetectorThrows()
    {
        var result = FileLockQuery.FindLockOwners([first], CancellationToken.None,
            (_, _) => throw new InvalidOperationException("system snapshot unavailable"),
            (_, _) => new([new LockingProcess { ProcessId = 101, ProcessName = "owner" }]));

        Assert.Single(result.Owners);
        Assert.Equal(1, result.CompletedTargetCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsFailure
            && diagnostic.Message.Contains("system snapshot unavailable"));
    }

    [Fact]
    public void OneRestartManagerExceptionDoesNotDiscardOtherTargetsOrHandleResults()
    {
        var result = FileLockQuery.FindLockOwners([first, second, folder], CancellationToken.None,
            (_, _) => new([new(new LockingProcess { ProcessId = 101, ProcessName = "folder-owner" }, [folder])],
                [first, second, folder], []),
            (path, _) => path == first ? throw new InvalidOperationException("one file failed")
                : new([new LockingProcess { ProcessId = 202, ProcessName = "file-owner" }]));

        Assert.Equal(2, result.Owners.Count);
        Assert.Equal(new[] { folder }, Assert.IsType<FileLockOwnerItem>(result.Owners.Single(i => i.ProcessId == 101)).TargetPaths);
        Assert.Equal(new[] { second }, Assert.IsType<FileLockOwnerItem>(result.Owners.Single(i => i.ProcessId == 202)).TargetPaths);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsFailure
            && diagnostic.TargetPath == first && diagnostic.Message.Contains("one file failed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedProcessAccessLimitsDoNotBecomeTargetFailures(bool hasOwner)
    {
        var limitation = new LockQueryDiagnostic(LockDiagnosticKind.CoverageLimitation,
            "ProcessHandleAccessDenied", "进程句柄访问被拒绝，关联目标未知", ProcessId: 999,
            Stage: "OpenProcess", RequestedAccess: 0x40, NativeError: 5, ProtectionLevel: "Unknown");

        var result = FileLockQuery.FindLockOwners([folder], CancellationToken.None,
            (_, _) => new(hasOwner
                ? [new(new LockingProcess { ProcessId = 101, ProcessName = "owner" }, [folder])]
                : [], [folder], [limitation]),
            (_, _) => throw new InvalidOperationException("Directories must not enter Restart Manager"));

        Assert.False(result.HasFailures);
        Assert.True(result.HasCoverageLimitations);
        Assert.Equal(1, result.CompletedTargetCount);
        Assert.Equal(hasOwner ? 1 : 0, result.Owners.Count);
        Assert.Equal(999, Assert.Single(result.Diagnostics).ProcessId);
        Assert.Null(result.Diagnostics[0].TargetPath);
    }

    [Fact]
    public void MetadataUnavailableDoesNotDiscardConfirmedFileOwnerOrCompletion()
    {
        var result = FileLockQuery.FindLockOwners([first], CancellationToken.None,
            (_, _) => new([], [], []),
            (_, _) => new([new LockingProcess { ProcessId = 101, ProcessName = "PID 101" }],
                [new(LockDiagnosticKind.MetadataUnavailable, "ProcessMetadataOpenFailed",
                    "进程基本信息不可用", ProcessId: 101, Stage: "OpenProcess", RequestedAccess: 0x1000, NativeError: 5)]));

        var owner = Assert.IsType<FileLockOwnerItem>(Assert.Single(result.Owners));
        Assert.Equal(101, owner.ProcessId);
        Assert.Equal("PID 101", owner.ProcessName);
        Assert.Equal(new[] { first }, owner.TargetPaths);
        Assert.Equal(1, result.CompletedTargetCount);
        Assert.False(result.HasFailures);
        Assert.False(result.HasCoverageLimitations);
        Assert.Equal(first, Assert.Single(result.Diagnostics).TargetPath);
    }

    [Fact]
    public void RestartManagerServiceLabelDoesNotReplaceBetterHandleMetadataWithFallbacks()
    {
        var result = FileLockQuery.FindLockOwners([first], CancellationToken.None,
            (_, _) => new([new(new LockingProcess
            {
                ProcessId = 101, ProcessName = "known-name", ExecutablePath = @"C:\known.exe"
            }, [first])], [first], []),
            (_, _) => new([new LockingProcess
            {
                ProcessId = 101, ProcessName = "PID 101", ServiceName = "service-name"
            }]));

        var owner = Assert.Single(result.Owners);
        Assert.Equal("known-name", owner.ProcessName);
        Assert.Equal(@"C:\known.exe", owner.ExecutablePath);
        Assert.Equal("service-name", owner.ServiceName);
    }

    [Fact]
    public void EmptyAndInvalidPathsHaveTargetFailuresRatherThanCoverageWarnings()
    {
        var result = FileLockQuery.FindLockOwners([" ", "bad\0path"], CancellationToken.None,
            (_, _) => throw new InvalidOperationException("No valid targets"),
            (_, _) => throw new InvalidOperationException("No valid targets"));

        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(LockDiagnosticKind.TargetFailure, diagnostic.Kind));
        Assert.True(result.HasFailures);
        Assert.False(result.HasCoverageLimitations);
    }

    [Fact]
    public void PreCanceledBatchDoesNotReachNativeDetectors()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => FileLockQuery.FindLockOwners([first], cancellation.Token,
            (_, _) => throw new InvalidOperationException("Handle scan must not run"),
            (_, _) => throw new InvalidOperationException("Restart Manager must not run")));
    }

    [Theory]
    [InlineData(@"\Device\Volume\Data", @"\Device\Volume\Data", true, true)]
    [InlineData(@"\Device\Volume\Data\child.txt", @"\Device\Volume\Data", true, true)]
    [InlineData(@"\device\volume\DATA\nested\file.txt", @"\Device\Volume\Data\", true, true)]
    [InlineData(@"\Device\Volume\Data-old\child.txt", @"\Device\Volume\Data", true, false)]
    [InlineData(@"\Device\Volume\Database\child.txt", @"\Device\Volume\Data", true, false)]
    [InlineData(@"\Device\Volume\Data\child.txt", @"\Device\Volume\Data", false, false)]
    [InlineData(@"\Device\Volume\file.txt", @"\Device\Volume\", true, true)]
    public void DirectoryMatchingRequiresAPathBoundary(string handle, string target, bool directory, bool expected)
    {
        Assert.Equal(expected, HandleFinder.MatchesPath(handle, target, directory));
    }

    public void Dispose()
    {
        File.Delete(first);
        File.Delete(second);
        Directory.Delete(folder);
        Directory.Delete(root);
    }
}
