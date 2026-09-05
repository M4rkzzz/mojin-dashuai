using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public class JavaLaunchArgumentsTests
{
    [Theory]
    [InlineData("m3e",8)]
    [InlineData("dc2",17)]
    [InlineData("mb",25)]
    [InlineData("vw",17)]
    public void OtherInstancesAndRuntimesKeepTheirArguments(string instance,int major)
        =>Assert.Equal("-Dplayer.option=true",JavaLaunchArguments.ForInstance(instance,major,"-Dplayer.option=true",32));

    [Theory]
    [InlineData(1,1,1,2)]
    [InlineData(2,2,1,2)]
    [InlineData(32,4,2,4)]
    public void FourthServerBoundsNativeWorkersWithoutOverridingHeapSize(int cpus,int parallel,int concurrent,int compiler)
    {
        var args=JavaLaunchArguments.ForInstance("vw",8,"",cpus);
        Assert.Contains("-XX:+UseG1GC",args);
        Assert.Contains($"-XX:ParallelGCThreads={parallel}",args);
        Assert.Contains($"-XX:ConcGCThreads={concurrent}",args);
        Assert.Contains($"-XX:CICompilerCount={compiler}",args);
        Assert.DoesNotContain("-Xmx",args);Assert.DoesNotContain("-Xms",args);
    }

    [Theory]
    [InlineData("-XX:+UseParallelGC -Dplayer.option=true")]
    [InlineData("-XX:+UseG1GC -XX:ParallelGCThreads=8")]
    [InlineData("-XX:-UseG1GC")]
    public void ExplicitCollectorChoiceIsPreserved(string custom)
        =>Assert.Equal(custom,JavaLaunchArguments.ForInstance("vw",8,custom,32));

    [Fact] public void IndividualTuningOverridesAreNotDuplicatedOrGivenAnIncompatibleFreeRatio()
    {
        const string custom="-XX:ParallelGCThreads=6 -XX:MinHeapFreeRatio=40 -Dplayer.option=true";
        var args=JavaLaunchArguments.ForInstance("vw",8,custom,32);
        Assert.EndsWith(custom,args);
        Assert.DoesNotContain("ParallelGCThreads=4",args);
        Assert.DoesNotContain("MaxHeapFreeRatio=20",args);
        Assert.Contains("-XX:+UseG1GC",args);
    }
}
