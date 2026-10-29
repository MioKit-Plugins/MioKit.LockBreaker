namespace MioKit.LockBreaker;

public static class FileLockFinderConst
{
    public const string PluginId = "miokit.lock-finder";

    public const string PluginTypeId = "D510BDCC-5B7A-443A-B7C3-41B11BFB3A75";
    public static readonly Guid PluginType = Guid.Parse(PluginTypeId);

    public const string FileLockSearchScopeId = "lock-finder.file-lock-scope";
    public const string FileLockSearchScopeTypeId = "A8F3C2E1-5B4D-4A9E-9C1F-7D6E8B0A2F45";
    public static readonly Guid FileLockSearchScopeType = Guid.Parse(FileLockSearchScopeTypeId);

    public const string PortLockSearchScopeId = "lock-finder.port-lock-scope";
    public const string PortLockSearchScopeTypeId = "B1C4D5E6-7F80-4A91-B2C3-D4E5F6071829";
    public static readonly Guid PortLockSearchScopeType = Guid.Parse(PortLockSearchScopeTypeId);

    public const string OpenFileLockWindowNodeId = "lock-finder.open-file-lock";
    public const string OpenFileLockWindowNodeTypeId = "C2D5E6F7-8091-4B02-A3B4-E5F60718293A";
    public static readonly Guid OpenFileLockWindowNodeType = Guid.Parse(OpenFileLockWindowNodeTypeId);

    public const string OpenPortLockWindowNodeId = "lock-finder.open-port-lock";
    public const string OpenPortLockWindowNodeTypeId = "D3E6F708-91A2-4C13-B4C5-F60718293A4B";
    public static readonly Guid OpenPortLockWindowNodeType = Guid.Parse(OpenPortLockWindowNodeTypeId);
}
