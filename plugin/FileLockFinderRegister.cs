namespace MioKit.LockBreaker;

public class FileLockFinderRegister : RegisterBase<FileLockFinderRegister>
{
    public override void RegisterService(ContainerBuilder builder, IServiceCollection services)
    {
        builder.RegisterType<Services.LockOperations>().As<Services.ILockOperations>().SingleInstance();
        builder.RegisterType<Services.LockWindowLauncher>().AsSelf().SingleInstance();
        var plugin = new FileLockFinderPlugin();

        builder.RegisterInstance(plugin)
            .As<IPlugin>()
            .Keyed<IPlugin>(FileLockFinderConst.PluginId)
            .As(plugin.GetType());
    }
}
