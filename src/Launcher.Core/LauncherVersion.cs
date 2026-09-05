using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public static class LauncherVersion
{
    private sealed record Parsed(string[] Numbers,string[] Prerelease);
    private static Parsed Parse(string version)
    {
        if(version.Length>200)throw new InvalidDataException("启动器版本号无效。");
        var match=Regex.Match(version,@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z");
        if(!match.Success)throw new InvalidDataException("启动器版本号无效。");
        var prerelease=match.Groups[4].Success?match.Groups[4].Value.Split('.'):[];
        if(prerelease.Any(p=>Numeric(p)&&p.Length>1&&p[0]=='0'))throw new InvalidDataException("启动器版本号无效。");
        return new([match.Groups[1].Value,match.Groups[2].Value,match.Groups[3].Value],prerelease);
    }
    private static bool Numeric(string value)=>value.All(c=>c is >= '0' and <= '9');
    private static int Number(string left,string right)=>left.Length!=right.Length?left.Length.CompareTo(right.Length):StringComparer.Ordinal.Compare(left,right);
    public static void Validate(string version)=>Parse(version);
    public static int Compare(string left,string right)
    {
        var a=Parse(left);var b=Parse(right);
        for(var i=0;i<3;i++){var result=Number(a.Numbers[i],b.Numbers[i]);if(result!=0)return result;}
        if(a.Prerelease.Length==0||b.Prerelease.Length==0)
            return a.Prerelease.Length==b.Prerelease.Length?0:a.Prerelease.Length==0?1:-1;
        for(var i=0;i<Math.Min(a.Prerelease.Length,b.Prerelease.Length);i++)
        {
            var x=a.Prerelease[i];var y=b.Prerelease[i];var xn=Numeric(x);var yn=Numeric(y);
            var result=xn&&yn?Number(x,y):xn!=yn?xn?-1:1:StringComparer.Ordinal.Compare(x,y);
            if(result!=0)return result;
        }
        return a.Prerelease.Length.CompareTo(b.Prerelease.Length);
    }
}
