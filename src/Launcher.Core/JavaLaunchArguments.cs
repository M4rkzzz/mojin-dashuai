using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public static class JavaLaunchArguments
{
    public static string ForInstance(string instance,int javaMajor,string custom,int processorCount)
    {
        if(instance!="vw"||javaMajor!=8)return custom;
        // Explicit collector choices are a complete player override. Other extra
        // arguments keep the defaults, with individually supplied values taking priority.
        if(Regex.IsMatch(custom,@"(?:^|\s)-XX:[+-]Use\w*GC(?:\s|$)"))return custom;
        var threads=Math.Clamp(processorCount,1,4);
        var defaults=new List<string>{"-XX:+UseG1GC"};
        void Add(string name,int value)
        {
            if(!Regex.IsMatch(custom,@"(?:^|\s)-XX:"+name+"="))defaults.Add($"-XX:{name}={value}");
        }
        Add("ParallelGCThreads",threads);
        Add("ConcGCThreads",Math.Min(2,Math.Max(1,threads/2)));
        Add("CICompilerCount",Math.Max(2,threads));
        if(!Regex.IsMatch(custom,@"(?:^|\s)-XX:(?:Min|Max)HeapFreeRatio="))
        {defaults.Add("-XX:MinHeapFreeRatio=10");defaults.Add("-XX:MaxHeapFreeRatio=20");}
        return string.Join(" ",defaults)+(string.IsNullOrWhiteSpace(custom)?"":" "+custom);
    }
}
