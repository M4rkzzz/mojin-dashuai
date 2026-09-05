package uk.boshan.mojin;

import cpw.mods.fml.client.FMLClientHandler;
import cpw.mods.fml.common.FMLCommonHandler;
import cpw.mods.fml.common.Mod;
import cpw.mods.fml.common.event.FMLInitializationEvent;
import cpw.mods.fml.common.eventhandler.SubscribeEvent;
import cpw.mods.fml.common.gameevent.TickEvent;
import org.apache.logging.log4j.Level;
import org.apache.logging.log4j.LogManager;
import org.apache.logging.log4j.core.LogEvent;
import org.apache.logging.log4j.core.Logger;
import org.apache.logging.log4j.core.filter.AbstractFilter;
import java.util.concurrent.atomic.AtomicBoolean;

/** Starts the selected connection only after Minecraft reaches its normal client tick loop. */
@Mod(modid = "mojinautoconnect", name = "Mojin Auto Connect", version = "0.1.0",
     acceptableRemoteVersions = "*", acceptedMinecraftVersions = "[1.7.10]")
public final class MojinAutoConnect {
    private int readyTicks;
    private boolean attempted;
    private String host;
    private int port;

    @Mod.EventHandler
    public void initialize(FMLInitializationEvent event) {
        if (!FMLCommonHandler.instance().getSide().isClient()) return;
        ((Logger) LogManager.getLogger("Angelica")).addFilter(new RepeatedRenderLogFilter());
        host = System.getProperty("mojin.join.host", "");
        if (host.isEmpty()) return; // Standard-pack imports retain the ordinary main menu.
        try {
            port = Integer.parseInt(System.getProperty("mojin.join.port", "25565"));
            if (!host.matches("[A-Za-z0-9.-]+") || port < 1 || port > 65535)
                throw new IllegalArgumentException("Invalid selected server");
            FMLCommonHandler.instance().bus().register(this);
        } catch (RuntimeException error) {
            throw new IllegalArgumentException("Mojin connection configuration is invalid", error);
        }
    }

    public static final class RepeatedRenderLogFilter extends AbstractFilter {
        private final AtomicBoolean seen = new AtomicBoolean();
        @Override
        public Result filter(LogEvent event) {
            if (event.getLevel() == Level.INFO && "Angelica".equals(event.getLoggerName())
                && "SKIPPING glBindTexture for target 32879".equals(event.getMessage().getFormattedMessage())
                && seen.getAndSet(true)) return Result.DENY;
            return Result.NEUTRAL;
        }
    }

    @SubscribeEvent
    public void onClientTick(TickEvent.ClientTickEvent event) {
        if (attempted || event.phase != TickEvent.Phase.END || ++readyTicks < 20) return;
        attempted = true;
        System.out.println("[Mojin] Client initialization complete; connecting to selected server.");
        FMLClientHandler.instance().connectToServerAtStartup(host, port);
    }
}
