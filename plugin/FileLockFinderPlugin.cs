using MioKit.LockBreaker.Nodes;

namespace MioKit.LockBreaker;

public class FileLockFinderPlugin : PluginBase
{
    public FileLockFinderPlugin() : base(FileLockFinderConst.PluginId)
    {
    }

    public override Guid MioType => FileLockFinderConst.PluginType;

    protected override async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSubTreeLoadedAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var fileScope = this.FindFirstDescendant<FileLockSearchScope>(n => n.Id == FileLockFinderConst.FileLockSearchScopeId)
            ?? new FileLockSearchScope(FileLockFinderConst.FileLockSearchScopeId);
        var portScope = this.FindFirstDescendant<PortLockSearchScope>(n => n.Id == FileLockFinderConst.PortLockSearchScopeId)
            ?? new PortLockSearchScope(FileLockFinderConst.PortLockSearchScopeId);
        var openFile = this.FindFirstDescendant<OpenFileLockWindowNode>(n => n.Id == FileLockFinderConst.OpenFileLockWindowNodeId)
            ?? new OpenFileLockWindowNode(FileLockFinderConst.OpenFileLockWindowNodeId);
        var openPort = this.FindFirstDescendant<OpenPortLockWindowNode>(n => n.Id == FileLockFinderConst.OpenPortLockWindowNodeId)
            ?? new OpenPortLockWindowNode(FileLockFinderConst.OpenPortLockWindowNodeId);
        var checkSelection = this.FindFirstDescendant<CheckExplorerSelectionNode>(n => n.Id == FileLockFinderConst.CheckExplorerSelectionNodeId)
            ?? new CheckExplorerSelectionNode(FileLockFinderConst.CheckExplorerSelectionNodeId);

        var data = Container!.Resolve<IMioDataProvider>();
        await data.BatchLoadValueAsync(
            [openFile, openPort, checkSelection],
            [
                MioFeatureExtensions.NameProperty,
                MioFeatureExtensions.DescriptionProperty,
                AliasNameExtension.AliasNameProperty,
                HotkeyExtension.HotkeyProperty,
                PinnedExtension.IsPinnedProperty,
                PinnedExtension.PinnedOrderProperty,
                RecentExtension.LastUseTimeProperty,
                RecentExtension.UseCountProperty
            ]);

        SetDefaults(fileScope, "文件占用", "根据剪贴板文件查看占用进程");
        SetDefaults(portScope, "端口占用", "输入 port-lock 并触发 command，再输入端口号查看占用");
        SetDefaults(openFile, "打开文件占用", "打开文件占用查询窗口");
        SetDefaults(openPort, "打开端口占用", "打开端口占用查询窗口");
        SetDefaults(checkSelection, "检查选中文件占用", "检查本次触发的资源管理器中选中的文件和文件夹占用，支持搜索、手势和轮盘");

        MioObject[] nodes = [fileScope, portScope, openFile, openPort, checkSelection];
        cancellationToken.ThrowIfCancellationRequested();
        await data.StoreManyAsync(nodes);
        foreach (var node in nodes)
            node.SetParent(this);
    }

    public override async Task EnsureSubTreeLoadedAsync()
    {
        await EnsureTreeLoadedAsync<FileLockSearchScope>(FileLockFinderConst.FileLockSearchScopeId);
        await EnsureTreeLoadedAsync<PortLockSearchScope>(FileLockFinderConst.PortLockSearchScopeId);
        await EnsureTreeLoadedAsync<OpenFileLockWindowNode>(FileLockFinderConst.OpenFileLockWindowNodeId);
        await EnsureTreeLoadedAsync<OpenPortLockWindowNode>(FileLockFinderConst.OpenPortLockWindowNodeId);
        await EnsureTreeLoadedAsync<CheckExplorerSelectionNode>(FileLockFinderConst.CheckExplorerSelectionNodeId);
    }

    private static void SetDefaults(MioObject node, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
            node.SetName(name);
        if (string.IsNullOrWhiteSpace(node.Description))
            node.SetDescription(description);
    }

    public override ValueTask<PluginDataCleanupPlan> CreateDataCleanupPlanAsync(
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new PluginDataCleanupPlan
        {
            AdditionalEavRootObjectIds = []
        });
    }
}
