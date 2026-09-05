import java.nio.channels.Selector;

/** Headless check using the exact bundled runtime and game working directory. */
public final class SelectorCheck {
    public static void main(String[] args) throws Exception {
        try (Selector selector = Selector.open()) {
            System.out.println("SELECTOR_OK");
        }
    }
}
