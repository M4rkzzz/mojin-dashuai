package uk.boshan.network;

import cpw.mods.fml.common.FMLCommonHandler;
import cpw.mods.fml.common.FMLLog;
import cpw.mods.fml.common.Loader;
import cpw.mods.fml.common.Mod;
import cpw.mods.fml.common.event.FMLInitializationEvent;
import cpw.mods.fml.common.eventhandler.SubscribeEvent;
import cpw.mods.fml.common.network.FMLNetworkEvent;
import io.netty.channel.Channel;
import io.netty.channel.ChannelOption;
import java.lang.reflect.Field;
import java.util.concurrent.atomic.AtomicBoolean;

/** Apply the existing 1.7.10 low-latency socket option using Forge events only. */
@Mod(modid="mojinlegacynetwork",name="Mojin Legacy Network",version="1.0.0",acceptedMinecraftVersions="[1.7.10]",acceptableRemoteVersions="*")
public final class LegacyNetworkFix {
    private final AtomicBoolean reportedFailure = new AtomicBoolean();
    @Mod.EventHandler public void initialize(FMLInitializationEvent event) {
        if (Loader.isModLoaded("hodgepodge")) return; // It already supplies this fix.
        FMLCommonHandler.instance().bus().register(this);
        FMLLog.info("[Mojin Network] TCP_NODELAY enabled for new client/server connections");
    }
    @SubscribeEvent public void client(FMLNetworkEvent.ClientConnectedToServerEvent event) { apply(event); }
    @SubscribeEvent public void server(FMLNetworkEvent.ServerConnectionFromClientEvent event) { apply(event); }
    private void apply(Object event) {
        try {
            Object manager = event.getClass().getField("manager").get(event);
            for (Class<?> type = manager.getClass(); type != null; type = type.getSuperclass()) {
                for (Field field : type.getDeclaredFields()) {
                    if (!Channel.class.isAssignableFrom(field.getType())) continue;
                    field.setAccessible(true); Channel channel = (Channel)field.get(manager);
                    if (channel != null) channel.config().setOption(ChannelOption.TCP_NODELAY, true);
                    return;
                }
            }
        } catch (Exception unavailable) { report(); }
        catch (LinkageError unavailable) { report(); }
    }
    private void report() {
        if (reportedFailure.compareAndSet(false,true)) FMLLog.warning("[Mojin Network] Socket option unavailable; connection continues with its existing settings");
    }
}
