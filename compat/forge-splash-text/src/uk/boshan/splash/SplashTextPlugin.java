package uk.boshan.splash;

import java.util.Map;
import cpw.mods.fml.relauncher.IFMLLoadingPlugin;

@IFMLLoadingPlugin.Name("Mojin Forge Splash Text")
@IFMLLoadingPlugin.MCVersion("1.7.10")
@IFMLLoadingPlugin.TransformerExclusions({"uk.boshan.splash."})
public final class SplashTextPlugin implements IFMLLoadingPlugin {
    public String[] getASMTransformerClass() { return new String[]{"uk.boshan.splash.SplashTextTransformer"}; }
    public String getModContainerClass() { return null; }
    public String getSetupClass() { return null; }
    public String getAccessTransformerClass() { return null; }
    public void injectData(Map<String, Object> data) { }
}
