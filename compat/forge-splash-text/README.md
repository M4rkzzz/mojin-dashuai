# Forge 1.7.10 / GregAPI loading text compatibility

Four-server r3 adds `mojin-forge-splash-text-1.0.0.jar` (Java 8).

GregAPI 6.17.06 writes the loading bar's message through reflection, bypassing
Forge's normal special-character filter. A configuration file's absolute path
can contain Chinese characters. Forge's splash font only binds its ASCII atlas,
so requesting a Unicode page throws `IllegalArgumentException` and aborts startup.

The transformer intercepts only the existing `Field.set` call inside
`gregapi.util.UT$LoadingBar.step(Object)`. It replaces unsupported characters in
the already-evaluated loading text with `?`, preserving the original short
circuit, null handling and exception boundary. Game translations, GUI scaling,
font mods, configuration paths and gameplay remain unchanged. Original source
is embedded in the JAR. No Forge or GregTech classes are bundled.

Build with a JDK supporting `--release 8` and the pinned game dependencies:

```powershell
python compat/forge-splash-text/build.py --jdk <JDK> --libraries <instance/libraries> --output <output.jar>
```

The build uses `javac --release 8`, so its compiler must support that option.
See `packs/acceptance/vw-r3-splash-regression.json` for the English/Chinese path
reproduction and the final Chinese-path startup and server-entry confirmation.
