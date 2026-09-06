import uk.boshan.join.JoinRuntime;

/** Reproduces Java 8 FilePermission canonicalization probing a named pipe. */
public final class NamedPipeSecurityCheck {
    public static void main(String[] args) throws Exception {
        System.setSecurityManager((SecurityManager) Class.forName("cpw.mods.fml.relauncher.FMLSecurityManager").newInstance());
        String ticket = JoinRuntime.requestTicket();
        if (!ticket.equals("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")) throw new AssertionError("Wrong fixture ticket");
        System.out.println("SECURITY_PIPE_PASS");
    }
}
