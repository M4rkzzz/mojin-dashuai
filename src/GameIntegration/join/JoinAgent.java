package uk.boshan.join;

import java.lang.instrument.Instrumentation;
import java.io.File;
import java.util.jar.JarFile;

/** Minimal entry point; all cross-loader hooks live in the bootstrap class loader. */
public final class JoinAgent {
    public static void premain(String arguments, Instrumentation instrumentation) throws Exception {
        File jar = new File(JoinAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI());
        instrumentation.appendToBootstrapClassLoaderSearch(new JarFile(jar));
        Class<?> runtime = Class.forName("uk.boshan.join.JoinRuntime", true, null);
        runtime.getMethod("install", Instrumentation.class).invoke(null, instrumentation);
    }
}
