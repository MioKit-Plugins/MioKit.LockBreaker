using System.Reflection;
using MioKit.LockBreaker.Nodes;
using MioKit.Sdk;
using Xunit;

namespace MioKit.LockBreaker.Tests;

[CollectionDefinition("SDK node store", DisableParallelization = true)]
public sealed class SdkNodeStoreCollection;

[Collection("SDK node store")]
public sealed class NodeCapabilityTests : IDisposable
{
    private readonly PropertyInfo storeFactory = typeof(MioObject).Assembly
        .GetType("MioKit.Sdk.StoreFactory", throwOnError: true)!
        .GetProperty("StoreFactoryFunc", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object? originalFactory;

    public NodeCapabilityTests()
    {
        originalFactory = storeFactory.GetValue(null);
        Func<MioObject, DesignMioStore> factory = owner => new DesignMioStore(owner);
        storeFactory.SetValue(null, factory);
    }

    [Fact]
    public void SelectionNodeSupportsSearchAndHostBindingCapabilities()
    {
        var node = new CheckExplorerSelectionNode("selection-test");

        Assert.IsAssignableFrom<IInvokeFeature>(node);
        Assert.IsAssignableFrom<IHotkeyFeature>(node);
        Assert.IsAssignableFrom<IPinnedFeature>(node);
        Assert.IsAssignableFrom<IUseFeature>(node);
        Assert.IsAssignableFrom<IAliasNameFeature>(node);
        Assert.IsAssignableFrom<ISearchableFeature>(node);
        Assert.IsAssignableFrom<IRequireTargetWindowFeature>(node);
        Assert.Contains("文件工具", NodePalette.GetCategories(node.GetType()));
    }

    [Theory]
    [InlineData(typeof(CheckExplorerSelectionNode))]
    [InlineData(typeof(OpenFileLockWindowNode))]
    [InlineData(typeof(OpenPortLockWindowNode))]
    public void WindowEntriesAskTheHostToFinishInteractionBeforeExecution(Type nodeType)
    {
        var node = (IInvocationPresentationFeature)Activator.CreateInstance(nodeType, "presentation-test")!;

        var presentation = node.GetPresentation(new InvocationPresentationRequest
        {
            Source = InvokeSource.SearchBox
        });

        Assert.Equal(InvocationInteraction.CompleteBeforeExecution, presentation.Interaction);
    }

    [Fact]
    public void PortScopeKeepsTheSearchInteractionForCommandEntry()
    {
        var node = new PortLockSearchScope("port-scope-test");

        Assert.Equal(InvocationInteraction.Continue,
            node.GetPresentation(new InvocationPresentationRequest { Source = InvokeSource.SearchBox }).Interaction);
    }

    [Theory]
    [InlineData(typeof(OpenFileLockWindowNode))]
    [InlineData(typeof(OpenPortLockWindowNode))]
    public void ManualWindowEntriesDoNotRequireAnExternalTarget(Type nodeType)
    {
        Assert.False(typeof(IRequireTargetWindowFeature).IsAssignableFrom(nodeType));
        Assert.Contains("占用查询", NodePalette.GetCategories(nodeType));
    }

    [Theory]
    [InlineData(typeof(CheckExplorerSelectionNode))]
    [InlineData(typeof(OpenFileLockWindowNode))]
    [InlineData(typeof(OpenPortLockWindowNode))]
    [InlineData(typeof(PortLockSearchScope))]
    public async Task CanceledInvocationStopsBeforeResolvingAnyHostServices(Type nodeType)
    {
        var node = (IInvokeFeature)Activator.CreateInstance(nodeType, "canceled-test")!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.InvokeAsync(
            new InvokeContext(InvokeSource.SearchBox) { CancellationToken = cancellation.Token }));
    }

    public void Dispose() => storeFactory.SetValue(null, originalFactory);
}
