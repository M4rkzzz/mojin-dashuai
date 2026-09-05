package uk.boshan.mojin;

import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.fml.client.FMLClientHandler;
import net.minecraftforge.fml.common.FMLCommonHandler;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.fml.common.event.FMLInitializationEvent;
import net.minecraftforge.fml.common.eventhandler.SubscribeEvent;
import net.minecraftforge.fml.common.gameevent.TickEvent;

/** Connect after startup has returned to the normal client loop. */
@Mod(modid = "mojinautoconnect", name = "Mojin Auto Connect", version = "0.1.0",
     acceptableRemoteVersions = "*", acceptedMinecraftVersions = "[1.12.2]", clientSideOnly = true)
public final class MojinAutoConnect {
    private int readyTicks;
    private boolean attempted;
    private String host;
    private int port;

    @Mod.EventHandler
    public void initialize(FMLInitializationEvent event) {
        if (!FMLCommonHandler.instance().getSide().isClient()) return;
        host = System.getProperty("mojin.join.host", "");
        if (host.isEmpty()) return;
        port = Integer.parseInt(System.getProperty("mojin.join.port", "25565"));
        if (!host.matches("[A-Za-z0-9.-]+") || port < 1 || port > 65535)
            throw new IllegalArgumentException("Invalid selected server");
        MinecraftForge.EVENT_BUS.register(this);
    }

    @SubscribeEvent
    public void onClientTick(TickEvent.ClientTickEvent event) {
        if (attempted || event.phase != TickEvent.Phase.END || ++readyTicks < 20) return;
        attempted = true;
        System.out.println("[Mojin] Client initialization complete; connecting to selected server.");
        FMLClientHandler.instance().connectToServerAtStartup(host, port);
    }
}
