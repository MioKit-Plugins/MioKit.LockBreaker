namespace MioKit.LockBreaker;

public class FileLockFinderRegister : RegisterBase<FileLockFinderRegister>
{
    public override void RegisterService(ContainerBuilder builder, IServiceCollection services)
    {
        var plugin = new FileLockFinderPlugin();

        builder.RegisterInstance(plugin)
            .As<IPlugin>()
            .Keyed<IPlugin>(FileLockFinderConst.PluginId)
            .As(plugin.GetType());
    }
}