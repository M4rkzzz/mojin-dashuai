package uk.boshan.splash;

/** Loading text only: never touches translation tables, GUI size, or game font renderers. */
public final class SplashText {
    private static int diagnostics;
    public static Object safe(Object value) {
        if (value == null) return null;
        String text = value.toString();
        String safe = ascii(text);
        if (!text.equals(safe) && diagnostics++ < 3) {
            // ASCII escaping keeps the diagnostic readable on Java 8 with a Chinese system code page.
            StringBuilder escaped = new StringBuilder();
            for (char c : text.toCharArray()) {
                if (c >= 32 && c <= 126) escaped.append(c);
                else escaped.append(String.format("\\u%04x", (int)c));
            }
            System.out.println("[MojinSplashFix] Unsupported loading text: " + escaped);
        }
        return safe;
    }
    static String ascii(String text) {
        StringBuilder out = new StringBuilder(text.length());
        for (char c : text.toCharArray()) out.append(c >= 32 && c <= 126 ? c : '?');
        return out.toString();
    }
}
