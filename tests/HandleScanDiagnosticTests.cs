using MioKit.LockBreaker.Models;
using Xunit;

namespace MioKit.LockBreaker.Tests;

public sealed class HandleScanDiagnosticTests
{
    [Theory]
    [InlineData("OpenProcess")]
    [InlineData("DuplicateHandle")]
    public void AnUnattributedAccessDenialOnlyLimitsCoverage(string stage)
    {
        var metadata = new ProcessMetadata(123, "PID 123", null, "Unknown", []);

        var diagnostic = HandleFinder.CreateAccessLimitation(123, stage, 5, metadata);

        Assert.NotNull(diagnostic);
        Assert.Equal(LockDiagnosticKind.CoverageLimitation, diagnostic.Kind);
        Assert.False(diagnostic.IsFailure);
        Assert.Null(diagnostic.TargetPath);
        Assert.Equal(123, diagnostic.ProcessId);
        Assert.Equal(stage, diagnostic.Stage);
        Assert.Equal(0x40u, diagnostic.RequestedAccess);
        Assert.Equal(5, diagnostic.NativeError);
        Assert.Equal("Unknown", diagnostic.ProtectionLevel);
        Assert.DoesNotContain("受保护", diagnostic.Message);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(87)]
    public void AnExitedProcessOrInvalidHandleDoesNotCreateAFailureOrWarning(int error)
    {
        Assert.Null(HandleFinder.CreateAccessLimitation(123, "DuplicateHandle", error,
            new(123, "PID 123", null, "Unknown", [])));
    }

    [Fact]
    public void AConfirmedProtectionLevelIsRetainedAsEvidence()
    {
        var diagnostic = HandleFinder.CreateAccessLimitation(123, "OpenProcess", 5,
            new(123, "protected-process", null, "WindowsLight", []));

        Assert.Equal("WindowsLight", diagnostic!.ProtectionLevel);
        Assert.Equal("protected-process", diagnostic.ProcessName);
        Assert.False(diagnostic.IsFailure);
    }

    [Fact]
    public void NativeNameQueryFailurePreservesNtStatusAsACoverageLimitation()
    {
        const int accessDenied = unchecked((int)0xC0000022);
        var diagnostic = HandleFinder.CreateNameQueryLimitation(123, accessDenied, 2,
            new(123, "PID 123", null, "Unknown", []));

        Assert.NotNull(diagnostic);
        Assert.Equal(LockDiagnosticKind.CoverageLimitation, diagnostic.Kind);
        Assert.Equal(accessDenied, diagnostic.NativeError);
        Assert.Equal("NtQueryObject", diagnostic.Stage);
        Assert.Contains("0xC0000022", diagnostic.Message);
        Assert.Null(diagnostic.TargetPath);
        Assert.False(diagnostic.IsFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0xC0000008))]
    public void UnnamedObjectsAndInvalidHandleRacesDoNotProduceNameQueryWarnings(int status)
    {
        Assert.Null(HandleFinder.CreateNameQueryLimitation(123, status, 1,
            new(123, "PID 123", null, "Unknown", [])));
    }
}
