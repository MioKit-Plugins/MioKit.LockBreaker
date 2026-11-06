using System.ComponentModel;
using MioKit.LockBreaker.Models;
using Xunit;

namespace MioKit.LockBreaker.Tests;

public sealed class ProcessMetadataReaderTests
{
    [Fact]
    public void MetadataUsesOnlyQueryLimitedAndClosesTheIndependentProcessHandle()
    {
        var api = new FakeApi { ImagePath = @"C:\Tools\known-process.exe", Protection = 0xFFFFFFFE };

        var metadata = ProcessMetadataReader.Read(123, api);

        Assert.Equal(new uint[] { 0x1000 }, api.RequestedAccess);
        Assert.Equal(1, api.CloseCount);
        Assert.Equal("known-process", metadata.ProcessName);
        Assert.Equal(@"C:\Tools\known-process.exe", metadata.ExecutablePath);
        Assert.Equal("None", metadata.ProtectionLevel);
        Assert.Empty(metadata.Diagnostics);
    }

    [Fact]
    public void QueryAccessDeniedPreservesPidAndLeavesProtectionUnknown()
    {
        var api = new FakeApi { OpenError = 5 };

        var metadata = ProcessMetadataReader.Read(123, api);
        var process = LockingProcess.FromMetadata(metadata, "Handle");

        Assert.Equal(123, process.ProcessId);
        Assert.Equal("PID 123", process.ProcessName);
        Assert.Null(process.ExecutablePath);
        Assert.Equal("Unknown", metadata.ProtectionLevel);
        Assert.Equal(0, api.CloseCount);
        var diagnostic = Assert.Single(metadata.Diagnostics);
        Assert.Equal(LockDiagnosticKind.MetadataUnavailable, diagnostic.Kind);
        Assert.False(diagnostic.IsFailure);
        Assert.Equal(5, diagnostic.NativeError);
        Assert.Equal(0x1000u, diagnostic.RequestedAccess);
    }

    [Fact]
    public void AnUnavailableImagePathDoesNotErasePidOrKnownProtectionLevel()
    {
        var api = new FakeApi { ImageError = 5, Protection = 3 };

        var metadata = ProcessMetadataReader.Read(456, api);

        Assert.Equal(456, metadata.ProcessId);
        Assert.Equal("PID 456", metadata.ProcessName);
        Assert.Equal("AntimalwareLight", metadata.ProtectionLevel);
        Assert.Equal(1, api.CloseCount);
        var diagnostic = Assert.Single(metadata.Diagnostics);
        Assert.Equal("QueryFullProcessImageName", diagnostic.Stage);
        Assert.Equal("AntimalwareLight", diagnostic.ProtectionLevel);
        Assert.Equal(LockDiagnosticKind.MetadataUnavailable, diagnostic.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedProtectionQueryDoesNotPretendAProcessIsProtectedOrHideItsName(bool throws)
    {
        var api = new FakeApi { ImagePath = @"C:\Tools\ordinary.exe", ProtectionError = 5, ThrowProtection = throws };

        var metadata = ProcessMetadataReader.Read(456, api);

        Assert.Equal("ordinary", metadata.ProcessName);
        Assert.Equal("Unknown", metadata.ProtectionLevel);
        Assert.Empty(metadata.Diagnostics);
        Assert.Equal(1, api.CloseCount);
    }

    [Fact]
    public void ImageQueryExceptionStillClosesTheHandleAndKeepsConfirmedPid()
    {
        var api = new FakeApi { ThrowImage = true };

        var metadata = ProcessMetadataReader.Read(456, api);

        Assert.Equal("PID 456", metadata.ProcessName);
        Assert.Equal(1, api.CloseCount);
        Assert.Equal(LockDiagnosticKind.MetadataUnavailable, Assert.Single(metadata.Diagnostics).Kind);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(87)]
    public void AProcessExitingDuringMetadataReadIsNotAQueryFailure(int error)
    {
        var metadata = ProcessMetadataReader.Read(456, new FakeApi { OpenError = error });

        Assert.Equal(456, metadata.ProcessId);
        Assert.Empty(metadata.Diagnostics);
        Assert.Equal("Unknown", metadata.ProtectionLevel);
    }

    [Fact]
    public void UnrecognizedProtectionValueRemainsUnknown()
    {
        var metadata = ProcessMetadataReader.Read(456, new FakeApi { Protection = 0xFFFFFFFF });

        Assert.Equal("Unknown", metadata.ProtectionLevel);
    }

    private sealed class FakeApi : IProcessMetadataApi
    {
        public int OpenError { get; init; }
        public int ImageError { get; init; }
        public int ProtectionError { get; init; }
        public bool ThrowImage { get; init; }
        public bool ThrowProtection { get; init; }
        public uint Protection { get; init; } = 0xFFFFFFFE;
        public string ImagePath { get; init; } = @"C:\known.exe";
        public List<uint> RequestedAccess { get; } = [];
        public int CloseCount { get; private set; }

        public IntPtr OpenProcess(uint access, int processId, out int error)
        {
            RequestedAccess.Add(access);
            error = OpenError;
            return error == 0 ? new IntPtr(1234) : IntPtr.Zero;
        }

        public bool TryGetImagePath(IntPtr process, out string? path, out int error)
        {
            if (ThrowImage) throw new Win32Exception(5);
            path = ImagePath;
            error = ImageError;
            return error == 0;
        }

        public bool TryGetProtectionLevel(IntPtr process, out uint level, out int error)
        {
            if (ThrowProtection) throw new Win32Exception(5);
            level = Protection;
            error = ProtectionError;
            return error == 0;
        }

        public void CloseProcess(IntPtr process)
        {
            Assert.Equal(new IntPtr(1234), process);
            CloseCount++;
        }
    }
}
