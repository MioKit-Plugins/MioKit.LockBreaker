using System.Reflection;
using MioKit.LockBreaker.Services;
using MioKit.Sdk;
using Vanara.PInvoke;
using Xunit;

namespace MioKit.LockBreaker.Tests;

public sealed class ExplorerSelectionResolverTests
{
    [Fact]
    public void UsesOnlyTheCapturedTargetAmongMultipleExplorerWindows()
    {
        var snapshot = CreateSnapshot(101, 1,
            Window(202, @"C:\Other\other.txt"),
            Window(101, @"C:\Target\file.txt", @"C:\Target\Folder"));

        var result = ExplorerSelectionResolver.Resolve(snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { @"C:\Target\file.txt", @"C:\Target\Folder" }, result.Paths);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(20)]
    public void ResolvesByCapturedHandleWhenActiveIndexIsWrongOrOutOfRange(int activeIndex)
    {
        var snapshot = CreateSnapshot(101, activeIndex,
            Window(202, @"C:\Other\other.txt"), Window(101, @"C:\Target\file.txt"));

        var result = ExplorerSelectionResolver.Resolve(snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { @"C:\Target\file.txt" }, result.Paths);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(20)]
    public void NeverFallsBackToAnotherExplorerWindow(int activeIndex)
    {
        var result = ExplorerSelectionResolver.Resolve(
            CreateSnapshot(101, activeIndex, Window(202, @"C:\Other\other.txt")));

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Paths);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void LegacySnapshotWithoutCaptureReportStillUsesItsSelection()
    {
        var result = ExplorerSelectionResolver.Resolve(
            CreateSnapshot(101, 0, Window(101, @"C:\Target\file.txt")));

        Assert.True(result.IsSuccess);
        Assert.Single(result.Paths);
    }

    [Fact]
    public void AcquiredSelectionReportAllowsCapturedPaths()
    {
        var snapshot = CreateSnapshot(101, 0, Window(101, @"C:\Target\file.txt"));
        SetReport(snapshot, CaptureStatus.Acquired);

        Assert.True(ExplorerSelectionResolver.Resolve(snapshot).IsSuccess);
    }

    [Theory]
    [InlineData(CaptureStatus.Empty)]
    [InlineData(CaptureStatus.Failed)]
    [InlineData(CaptureStatus.Unavailable)]
    [InlineData(CaptureStatus.SkippedByPolicy)]
    [InlineData(CaptureStatus.NotRequested)]
    public void RejectsUnsuccessfulSelectionCaptureEvenIfPathsRemain(CaptureStatus status)
    {
        var snapshot = CreateSnapshot(101, 0, Window(101, @"C:\Target\file.txt"));
        SetReport(snapshot, status);

        var result = ExplorerSelectionResolver.Resolve(snapshot);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Paths);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ReadsSelectionResourceRatherThanDirectoryOrCompositeResource()
    {
        var snapshot = CreateSnapshot(101, 0, Window(101, @"C:\Target\file.txt"));
        SetReport(snapshot, new Dictionary<CaptureResources, ResourceCaptureResult>
        {
            [CaptureResources.ExplorerDirectory] = Capture(CaptureStatus.Acquired),
            [CaptureResources.ExplorerSelection] = Capture(CaptureStatus.Failed),
            [CaptureResources.Explorer] = Capture(CaptureStatus.Acquired)
        });

        Assert.False(ExplorerSelectionResolver.Resolve(snapshot).IsSuccess);
    }

    [Fact]
    public void TrimsPathsRemovesBlankEntriesAndDeduplicatesCaseInsensitively()
    {
        var snapshot = CreateSnapshot(101, 0,
            Window(101, " ", "", null!, @" C:\Data\File.txt ", @"c:\data\FILE.TXT", @" C:\Data\Folder "));

        var result = ExplorerSelectionResolver.Resolve(snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { @"C:\Data\File.txt", @"C:\Data\Folder" }, result.Paths);
    }

    [Fact]
    public void RejectsAnEmptySelection()
    {
        var result = ExplorerSelectionResolver.Resolve(CreateSnapshot(101, 0, Window(101)));

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Paths);
        Assert.Contains("没有选中", result.Error!);
    }

    [Fact]
    public void RejectsMissingSnapshotAndMissingExplorerData()
    {
        Assert.False(ExplorerSelectionResolver.Resolve(null).IsSuccess);
        var snapshot = CreateSnapshot(101, 0, Window(101, @"C:\Target\file.txt"));
        Set(snapshot, nameof(InvocationSnapshot.Explorer), null);

        Assert.False(ExplorerSelectionResolver.Resolve(snapshot).IsSuccess);
    }

    [Fact]
    public void RejectsAWindowThatIsNotExplorerEvenWithExplorerSelectionData()
    {
        var snapshot = CreateSnapshot(101, 0, Window(101, @"C:\Target\file.txt"));
        Set(snapshot.ForegroundWindow, nameof(ForegroundWindowInfo.IsExplorerWindow), false);

        Assert.False(ExplorerSelectionResolver.Resolve(snapshot).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsMissingOrNullTargetHandles(bool missingHandle)
    {
        var snapshot = CreateSnapshot(101, 0, Window(101, @"C:\Target\file.txt"));
        Set(snapshot.ForegroundWindow, nameof(ForegroundWindowInfo.Handle),
            missingHandle ? null : new HWND(IntPtr.Zero));

        Assert.False(ExplorerSelectionResolver.Resolve(snapshot).IsSuccess);
    }

    [Fact]
    public void DoesNotUseADesktopViewWithAMatchingHandle()
    {
        var desktop = Window(101, @"C:\Users\Test\Desktop\file.txt");
        desktop.IsDesktop = true;

        Assert.False(ExplorerSelectionResolver.Resolve(CreateSnapshot(101, 0, desktop)).IsSuccess);
    }

    private static InvocationSnapshot CreateSnapshot(int targetHandle, int activeIndex,
        params ExplorerWindowInfo[] windows)
    {
        var foreground = new ForegroundWindowInfo();
        Set(foreground, nameof(ForegroundWindowInfo.Handle), new HWND((IntPtr)targetHandle));
        Set(foreground, nameof(ForegroundWindowInfo.IsExplorerWindow), true);
        var snapshot = new InvocationSnapshot();
        Set(snapshot, nameof(InvocationSnapshot.ForegroundWindow), foreground);
        Set(snapshot, nameof(InvocationSnapshot.Explorer), new ExplorerContext
        {
            ActiveIndex = activeIndex,
            ExplorerWindowSource = windows.ToList()
        });
        return snapshot;
    }

    private static ExplorerWindowInfo Window(int handle, params string[] paths) => new()
    {
        Handle = new HWND((IntPtr)handle),
        SelectedPaths = paths
    };

    private static ResourceCaptureResult Capture(CaptureStatus status) =>
        new(status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static void SetReport(InvocationSnapshot snapshot, CaptureStatus status) =>
        SetReport(snapshot, new Dictionary<CaptureResources, ResourceCaptureResult>
        {
            [CaptureResources.ExplorerSelection] = Capture(status)
        });

    private static void SetReport(InvocationSnapshot snapshot,
        Dictionary<CaptureResources, ResourceCaptureResult> resources)
    {
        var report = Activator.CreateInstance(typeof(InvocationCaptureReport),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [resources, new Dictionary<string, ResourceCaptureResult>()], null);
        Set(snapshot, nameof(InvocationSnapshot.CaptureReport), report);
    }

    private static void Set(object owner, string property, object? value) =>
        owner.GetType().GetProperty(property)!.SetValue(owner, value);
}
