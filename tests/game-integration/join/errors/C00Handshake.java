package net.minecraft.network.handshake.client;

/** Fixed client handshake shape; the production agent transforms this constructor. */
public final class C00Handshake {
    public enum Intention { STATUS, LOGIN }
    public String host;
    public Intention intention;
    public C00Handshake(String host, Intention intention) { this.host = host; this.intention = intention; }
}
