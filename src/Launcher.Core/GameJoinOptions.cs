namespace Boshan.Launcher;

public sealed record GameJoinOptions(string AgentFile,string PipeName,string Instance)
{
    public string Arguments()
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(AgentFile,@"^\.hub/join/agent-[a-f0-9]{64}\.jar$")
            ||!System.Text.RegularExpressions.Regex.IsMatch(PipeName,"^mojin-join-[a-f0-9]{32}$")
            ||Instance is not ("m3e" or "dc2" or "mb" or "vw"))throw new InvalidDataException("入服认证组件配置无效。");
        return $"-javaagent:{AgentFile} -Dmojin.join.pipe={PipeName} -Dmojin.join.instance={Instance}";
    }
}
