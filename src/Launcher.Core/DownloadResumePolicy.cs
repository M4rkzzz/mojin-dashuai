namespace Boshan.Launcher;

public static class DownloadResumePolicy
{
    public static ReleaseRef SelectRelease(ServerCatalog server,ReleaseRef saved,bool rollback)
    {
        if(!rollback)return server.Release??throw new InvalidDataException("此世界当前不可下载。");
        var release=server.Rollbacks.SingleOrDefault(r=>r.Sha256==saved.Sha256&&r.Compatibility==server.Release?.Compatibility);
        return release??throw new InvalidDataException("此回退版本已停止提供，请取消当前任务后更新客户端。");
    }
}
