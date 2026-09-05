import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

// Headless path check: never starts Minecraft, opens a window or connects to a server.
public final class ContentPathProbe {
    public static void main(String[] args) throws Exception {
        Path root=Paths.get(args[0]);
        byte[] expected="中文路径与玩家设置".getBytes(StandardCharsets.UTF_8);
        Path file=root.resolve("中文配置 with spaces.txt");
        Files.write(file,expected);
        if(!Arrays.equals(expected,Files.readAllBytes(file)))throw new Exception("Unicode file round trip failed");
        Class.forName(args[1],false,ContentPathProbe.class.getClassLoader());
        System.load(args[2]);
        System.out.println("PATH_CHECK_OK");
    }
}
