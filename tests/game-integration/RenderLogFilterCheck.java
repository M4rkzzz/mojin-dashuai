import org.apache.logging.log4j.LogManager;
import org.apache.logging.log4j.Level;
import org.apache.logging.log4j.core.Logger;
import uk.boshan.mojin.MojinAutoConnect.RepeatedRenderLogFilter;

public final class RenderLogFilterCheck {
    public static void main(String[] args) {
        Logger logger = (Logger) LogManager.getLogger("Angelica");
        logger.setLevel(Level.INFO);
        logger.addFilter(new RepeatedRenderLogFilter());
        for (int i = 0; i < 10; i++) logger.info("SKIPPING glBindTexture for target {}", 32879);
        logger.warn("SKIPPING glBindTexture for target 32879");
        logger.error("SKIPPING glBindTexture for target 32879");
        logger.info("Unrelated render diagnostic");
        logger.info("SKIPPING glBindTexture for target {}", 1234);
    }
}
