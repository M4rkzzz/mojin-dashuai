import net.minecraft.network.handshake.client.C00Handshake;

public final class JoinErrorCheck {
    public static void main(String[] args) throws Exception {
        C00Handshake status = new C00Handshake("status.example", C00Handshake.Intention.STATUS);
        if (!status.host.equals("status.example")) throw new AssertionError("Status handshake changed");
        try {
            C00Handshake login = new C00Handshake("play.example", C00Handshake.Intention.LOGIN);
            if (!args[0].equals("SUCCESS") || !login.host.equals("play.example\0MOJIN1\0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))
                throw new AssertionError("Expected rejection or unchanged valid ticket wire format");
        } catch (IllegalStateException error) {
            if (!args[0].equals(error.getMessage())) throw new AssertionError("Player message differs");
            if (!error.toString().equals(error.getMessage())) throw new AssertionError("Technical exception class exposed");
            if (error.getCause() != null) throw new AssertionError("Internal exception cause exposed");
        }
        System.out.println("PLAYER_MESSAGE_PASS");
    }
}
