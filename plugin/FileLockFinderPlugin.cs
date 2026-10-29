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
        var fileScope = this.FindFirstDescendant<FileLockSearchScope>(n => n.Id == FileLockFinderConst.FileLockSearchScopeId)
            ?? new FileLockSearchScope(FileLockFinderConst.FileLockSearchScopeId);
        fileScope.SetName("文件占用");
        fileScope.SetDescription("根据剪贴板文件查看占用进程");
        fileScope.SetParent(this);

        var portScope = this.FindFirstDescendant<PortLockSearchScope>(n => n.Id == FileLockFinderConst.PortLockSearchScopeId)
            ?? new PortLockSearchScope(FileLockFinderConst.PortLockSearchScopeId);
        portScope.SetName("端口占用");
        portScope.SetDescription("输入 port 并触发 command，再输入端口号查看占用");
        portScope.SetParent(this);

        var openFile = this.FindFirstDescendant<OpenFileLockWindowNode>(n => n.Id == FileLockFinderConst.OpenFileLockWindowNodeId)
            ?? new OpenFileLockWindowNode(FileLockFinderConst.OpenFileLockWindowNodeId);
        openFile.SetName("打开文件占用");
        openFile.SetDescription("打开文件占用查询窗口");
        openFile.SetParent(this);

        var openPort = this.FindFirstDescendant<OpenPortLockWindowNode>(n => n.Id == FileLockFinderConst.OpenPortLockWindowNodeId)
            ?? new OpenPortLockWindowNode(FileLockFinderConst.OpenPortLockWindowNodeId);
        openPort.SetName("打开端口占用");
        openPort.SetDescription("打开端口占用查询窗口");
        openPort.SetParent(this);

        var data = MioIoc.Resolve<IMioDataProvider>();
        await data.BatchLoadValueAsync(
            [openFile, openPort],
            [
                MioFeatureExtensions.NameProperty,
                MioFeatureExtensions.DescriptionProperty,
                AliasNameExtension.AliasNameProperty
            ]);

        await Task.CompletedTask;
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
