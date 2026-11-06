using System.Reflection;
using MioKit.LockBreaker.Nodes;
using MioKit.Sdk;
using Xunit;

namespace MioKit.LockBreaker.Tests;

[Collection("SDK node store")]
public sealed class FileLockSearchScopeTests : IDisposable
{
    private readonly PropertyInfo _storeFactory = typeof(MioObject).Assembly
        .GetType("MioKit.Sdk.StoreFactory", throwOnError: true)!
        .GetProperty("StoreFactoryFunc", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object? _originalFactory;

    public FileLockSearchScopeTests()
    {
        _originalFactory = _storeFactory.GetValue(null);
        _storeFactory.SetValue(null, (Func<MioObject, DesignMioStore>)(owner => new DesignMioStore(owner)));
    }

    [Fact]
    public void EmptyTextPreSearchCarriesAllClipboardFilesAndFoldersToExecution()
    {
        var paths = new List<string> { @"C:\Data\one.txt", @"D:\Data\two.txt", @"C:\Data\Folder" };
        var request = Request(paths);
        var node = new OpenFileLockWindowNode("clipboard-target");

        var result = FileLockSearchScope.CreateClipboardResult(request, node);

        Assert.True(request.IsEmptyText);
        Assert.True(request.IsFirstOpenWindowSearch);
        Assert.NotNull(result);
        Assert.Same(node, result.OwnerObject);
        Assert.Equal(SortPriority.Top, result.PinMode);
        Assert.Contains("3 项", result.Title);
        Assert.All(paths, path => Assert.Contains(path, result.Description!));
        var invocation = InvokeContext.FromSearchBox(result);
        Assert.Equal(paths, OpenFileLockWindowNode.ResolveTargetPaths(invocation.SearchResult?.Payload));
    }

    [Fact]
    public void BlankFirstEntryDoesNotHideLaterTargetsAndDuplicatesDoNotInflateTheCount()
    {
        var request = Request([" ", @" C:\Data\one.txt ", @"c:\data\ONE.TXT", null!, @"D:\Folder"]);

        var result = FileLockSearchScope.CreateClipboardResult(request, new OpenFileLockWindowNode("clipboard-target"));

        Assert.NotNull(result);
        Assert.Contains("2 项", result.Title);
        Assert.Equal(new[] { @"C:\Data\one.txt", @"D:\Folder" },
            OpenFileLockWindowNode.ResolveTargetPaths(result.Payload));
    }

    [Fact]
    public void AbbreviatingTheDescriptionKeepsEveryTargetInThePayload()
    {
        var paths = Enumerable.Range(1, 8).Select(index => $@"C:\Data\{index}.txt").ToList();

        var result = FileLockSearchScope.CreateClipboardResult(Request(paths), new OpenFileLockWindowNode("clipboard-target"));

        Assert.NotNull(result);
        Assert.Contains("8 项", result.Title);
        Assert.Contains("另有 5 项", result.Description!);
        Assert.Equal(paths, OpenFileLockWindowNode.ResolveTargetPaths(result.Payload));
    }

    [Fact]
    public void ExecutionUsesTheBatchCapturedWhenTheSearchResultWasCreated()
    {
        var files = new List<string> { @"C:\Original\one.txt", @"C:\Original\two.txt" };
        var result = FileLockSearchScope.CreateClipboardResult(Request(files), new OpenFileLockWindowNode("clipboard-target"));
        Assert.NotNull(result);
        files.Clear();
        files.Add(@"C:\New\other.txt");

        var invocation = InvokeContext.FromSearchBox(result);

        Assert.Equal(new[] { @"C:\Original\one.txt", @"C:\Original\two.txt" },
            OpenFileLockWindowNode.ResolveTargetPaths(invocation.SearchResult?.Payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingOrBlankClipboardFilesDoNotCreateAResult(string? path)
    {
        var request = Request(path is null ? null : [path]);

        Assert.Null(FileLockSearchScope.CreateClipboardResult(request, new OpenFileLockWindowNode("clipboard-target")));
    }

    [Fact]
    public void SingleFileKeepsItsExistingTitleAndLegacyStringPayloadStillWorks()
    {
        const string path = @"C:\Data\single.txt";
        var result = FileLockSearchScope.CreateClipboardResult(Request([path]), new OpenFileLockWindowNode("clipboard-target"));

        Assert.NotNull(result);
        Assert.Equal("查看文件占用（剪贴板）", result.Title);
        Assert.Equal(path, result.Description);
        Assert.Equal(new[] { path }, OpenFileLockWindowNode.ResolveTargetPaths(path));
        Assert.Null(OpenFileLockWindowNode.ResolveTargetPaths(null));
        Assert.Null(OpenFileLockWindowNode.ResolveTargetPaths(Array.Empty<string>()));
    }

    private static SearchRequest Request(List<string>? files)
    {
        var clipboard = new ClipboardSnapshot();
        typeof(ClipboardSnapshot).GetProperty(nameof(ClipboardSnapshot.Files))!.SetValue(clipboard, files);
        var snapshot = new InvocationSnapshot();
        typeof(InvocationSnapshot).GetProperty(nameof(InvocationSnapshot.Clipboard))!.SetValue(snapshot, clipboard);
        return new SearchRequest { Context = snapshot, SearchText = string.Empty, SearchNumber = 0 };
    }

    public void Dispose() => _storeFactory.SetValue(null, _originalFactory);
}
