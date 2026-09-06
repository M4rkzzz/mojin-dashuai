package uk.boshan.activities;

import java.io.File;
import java.lang.instrument.Instrumentation;
import java.util.jar.JarFile;

public final class ActivityAgent {
    public static void premain(String args, Instrumentation instrumentation) throws Exception {
        File jar = new File(ActivityAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI());
        instrumentation.appendToBootstrapClassLoaderSearch(new JarFile(jar));
        Class.forName("uk.boshan.activities.ActivityRuntime", true, null).getMethod("install", Instrumentation.class).invoke(null, instrumentation);
    }
}
