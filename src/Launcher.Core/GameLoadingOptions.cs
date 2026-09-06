namespace Boshan.Launcher;

public sealed record GameLoadingOptions(string AgentFile,string Session)
{
    public string Arguments()
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(AgentFile,@"^\.hub/loading/agent-[a-f0-9]{64}\.jar$")
            ||!System.Text.RegularExpressions.Regex.IsMatch(Session,"^[a-f0-9]{32}$"))
            throw new InvalidDataException("加载界面组件路径无效。");
        return $"-javaagent:{AgentFile} -Dmojin.loading.session={Session}";
    }
}
