package uk.boshan.join;

import java.io.*;
import java.lang.instrument.*;
import java.lang.reflect.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.regex.*;
import org.objectweb.asm.*;

/** Login-only, no tick polling, no credentials exposed to the Minecraft process. */
public final class JoinRuntime {
    private static final String MARKER = "\0MOJIN1\0";
    private static final String HOOK = "uk/boshan/join/JoinRuntime";
    private static Instrumentation instrumentation;
    private static boolean server;
    private static volatile String mode = "client";
    private static String instance, endpoint, secret, configPath, launcherPipe;
    private static volatile long configStamp = Long.MIN_VALUE;
    private static final Map<Object, Gate> gates = Collections.synchronizedMap(new WeakHashMap<Object, Gate>());
    private static final Set<Object> openedModules = Collections.newSetFromMap(new ConcurrentHashMap<Object, Boolean>());
    private static final ExecutorService workers = new ThreadPoolExecutor(0, 32, 30, TimeUnit.SECONDS,
            new SynchronousQueue<Runnable>(), new ThreadFactory() {
                public Thread newThread(Runnable r) { Thread t = new Thread(r, "Mojin Join Authentication"); t.setDaemon(true); return t; }
            }, new ThreadPoolExecutor.AbortPolicy());
    private static final class Gate { String ticket; boolean pending, admitted, loginSeen; Object resumePacket; }

    public static void install(Instrumentation inst) throws Exception {
        instrumentation = inst;
        String config = System.getProperty("mojin.join.server.config", "");
        server = !config.isEmpty();
        if (server) {
            configPath = config;
            Properties p = new Properties();
            try (Reader reader = new InputStreamReader(new FileInputStream(config), StandardCharsets.UTF_8)) { p.load(reader); }
            mode = p.getProperty("mode", "off").trim();
            instance = p.getProperty("instance", "").trim();
            endpoint = p.getProperty("redeemUrl", "").trim();
            secret = p.getProperty("secret", "").trim();
            if (!Arrays.asList("off", "observe", "enforce").contains(mode)) throw new IllegalArgumentException("Invalid join mode");
            {
                if (!validInstance(instance) || secret.length() < 24) throw new IllegalArgumentException("Missing join server configuration");
                URI uri = URI.create(endpoint);
                boolean loopback = "127.0.0.1".equals(uri.getHost()) || "localhost".equals(uri.getHost()) || "[::1]".equals(uri.getHost()) || "::1".equals(uri.getHost());
                boolean localContainer = Boolean.parseBoolean(p.getProperty("allowLocalContainerHttp", "false"))
                        && "hub-api".equals(uri.getHost()) && uri.getPort() == 8080 && "/internal/v1/join/redeem".equals(uri.getPath());
                if ((!"https".equals(uri.getScheme()) && !("http".equals(uri.getScheme()) && (loopback || localContainer))) || uri.getUserInfo() != null || uri.getQuery() != null || uri.getFragment() != null)
                    throw new IllegalArgumentException("Join endpoint requires HTTPS or loopback HTTP");
            }
            configStamp = new File(configPath).lastModified();
        } else {
            instance = System.getProperty("mojin.join.instance", "");
            launcherPipe = System.getProperty("mojin.join.pipe", "");
            if (!validInstance(instance) || !launcherPipe.matches("mojin-join-[a-fA-F0-9]{32}"))
                throw new IllegalArgumentException("Missing launcher join IPC configuration");
            JoinStartupConnection.install(inst);
        }
        ClassFileTransformer transformer = (ClassFileTransformer) java.lang.reflect.Proxy.newProxyInstance(JoinRuntime.class.getClassLoader(),
                new Class<?>[]{ClassFileTransformer.class}, new InvocationHandler() {
                    public Object invoke(Object proxy, Method method, Object[] args) throws Throwable {
                        if (!method.getName().equals("transform")) return null;
                        int offset = args.length == 6 ? 1 : 0;
                        String name = (String) args[offset + 1];
                        if (!target(name)) return null;
                        try {
                            if (offset == 1 && args[0] != null) openModule(args[0]);
                            return transform(name, (byte[]) args[offset + 4]);
                        } catch (Throwable error) {
                            log("fatal unsupported authentication hook " + name + " category=" + error.getClass().getSimpleName());
                            // Instrumentation silently ignores thrown transformer errors. Invalid
                            // bytes deliberately prevent this networking class from loading instead.
                            return new byte[]{0};
                        }
                    }
                });
        inst.addTransformer(transformer, false);
        log("ready mode=" + mode + " instance=" + instance);
    }

    private static boolean validInstance(String value) { return Arrays.asList("m3e", "dc2", "mb", "vw").contains(value); }
    private static boolean target(String name) {
        return name != null && (name.equals("net/minecraft/network/NetworkManager") || name.equals("net/minecraft/network/Connection")
                || name.equals("net/minecraft/network/handshake/client/C00Handshake")
                || name.equals("net/minecraft/network/protocol/handshake/ClientIntentionPacket"));
    }
    private static void openModule(Object module) throws Exception {
        if (!openedModules.add(module)) return;
        Class<?> moduleType = Class.forName("java.lang.Module");
        Object ourModule = Class.class.getMethod("getModule").invoke(JoinRuntime.class);
        @SuppressWarnings("unchecked") Set<String> packages = (Set<String>) moduleType.getMethod("getPackages").invoke(module);
        Map<String, Set<Object>> opens = new HashMap<String, Set<Object>>();
        for (String p : packages) if (p.startsWith("net.minecraft.network") || p.startsWith("net.minecraft.server.network")) opens.put(p, Collections.singleton(ourModule));
        Instrumentation.class.getMethod("redefineModule", moduleType, Set.class, Map.class, Map.class, Set.class, Map.class)
                .invoke(instrumentation, module, Collections.singleton(ourModule), Collections.emptyMap(), opens, Collections.emptySet(), Collections.emptyMap());
    }
    public static byte[] transform(final String name, byte[] bytes) {
        final boolean network = name.endsWith("/NetworkManager") || name.endsWith("/Connection");
        if ((network && !server) || (!network && server)) return null;
        ClassReader reader = new ClassReader(bytes);
        ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        final int[] count = {0};
        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            public MethodVisitor visitMethod(int access, String methodName, String descriptor, String signature, String[] exceptions) {
                MethodVisitor mv = super.visitMethod(access, methodName, descriptor, signature, exceptions);
                if (network && descriptor.startsWith("(Lio/netty/channel/ChannelHandlerContext;L") && descriptor.endsWith(")V")
                        && org.objectweb.asm.Type.getArgumentTypes(descriptor).length == 2
                        && !descriptor.contains("Ljava/lang/Object;") && !descriptor.contains("Ljava/lang/Throwable;")) {
                    count[0]++;
                    return new MethodVisitor(Opcodes.ASM9, mv) {
                        public void visitCode() {
                            super.visitCode();
                            mv.visitVarInsn(Opcodes.ALOAD, 0); mv.visitVarInsn(Opcodes.ALOAD, 1); mv.visitVarInsn(Opcodes.ALOAD, 2);
                            mv.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "read", "(Ljava/lang/Object;Ljava/lang/Object;Ljava/lang/Object;)Z", false);
                            Label pass = new Label(); mv.visitJumpInsn(Opcodes.IFEQ, pass); mv.visitInsn(Opcodes.RETURN);
                            mv.visitLabel(pass); mv.visitFrame(Opcodes.F_SAME, 0, null, 0, null);
                        }
                    };
                }
                if (!network && methodName.equals("<init>") && descriptor.contains("Ljava/lang/String;")) {
                    count[0]++;
                    return new MethodVisitor(Opcodes.ASM9, mv) {
                        public void visitInsn(int opcode) {
                            if (opcode == Opcodes.RETURN) { mv.visitVarInsn(Opcodes.ALOAD, 0); mv.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "clientHandshake", "(Ljava/lang/Object;)V", false); }
                            super.visitInsn(opcode);
                        }
                    };
                }
                return mv;
            }
        }, 0);
        if ((network && count[0] != 1) || (!network && count[0] < 1)) throw new IllegalStateException("Unsupported join hook shape: " + name + " matches=" + count[0]);
        log("hook installed " + name.substring(name.lastIndexOf('/') + 1));
        return writer.toByteArray();
    }

    public static void clientHandshake(Object packet) {
        if (server) return;
        String stage = "handshake_intention";
        try {
            if (!loginIntention(packet)) return;
            stage = "handshake_host";
            Field hostField = stringField(packet.getClass());
            String host = (String) hostField.get(packet);
            if (host != null && host.contains(MARKER)) return;
            if (host == null) throw new IOException("Invalid login handshake");
            stage = "launcher_ipc";
            String ticket = requestTicket();
            stage = "handshake_attach";
            String updated = host + MARKER + ticket;
            // Forge subsequently appends its own FML marker. Reserve those bytes too.
            if (updated.length() > 239) throw new IOException("Server address too long for authenticated handshake");
            hostField.set(packet, updated);
            log("ticket attached instance=" + instance);
        } catch (Exception e) {
            Throwable cause = e;
            while ((cause instanceof ExecutionException || cause instanceof InvocationTargetException) && cause.getCause() != null) cause = cause.getCause();
            if (cause instanceof JoinFailure) stage = ((JoinFailure)cause).stage;
            while (cause.getCause() != null) cause = cause.getCause();
            log("client failure stage=" + stage + " category=" + cause.getClass().getSimpleName() + " detail=" + systemFailure(cause));
            throw new IllegalStateException("入服认证失败，请保持魔金大帅统一客户端运行并重新登录。", e instanceof InvocationTargetException ? null : safeCause(e));
        }
    }
    private static final class JoinFailure extends IOException {
        final String stage;
        JoinFailure(String stage, Exception cause) { super("Join IPC failed", cause); this.stage = stage; }
    }
    private static Throwable safeCause(Exception e) { return new IOException(e instanceof TimeoutException ? "Join IPC timeout" : "Join IPC unavailable"); }
    private static String systemFailure(Throwable error) {
        String text = String.valueOf(error.getMessage()).toLowerCase(Locale.ROOT);
        if (text.contains("cannot find") || text.contains("找不到")) return "not_found";
        if (text.contains("access is denied") || text.contains("拒绝访问")) return "access_denied";
        if (text.contains("pipe instances are busy") || text.contains("管道实例都在使用") || text.contains("管道范例都在使用")) return "pipe_busy";
        if (text.contains("syntax is incorrect") || text.contains("语法不正确")) return "invalid_name";
        if (text.contains("pipe is being closed") || text.contains("管道正在被关闭")) return "pipe_closing";
        return "unspecified";
    }
    public static String requestTicket() throws Exception {
        final String pipe = System.getProperty("mojin.join.pipe", "");
        if (!pipe.matches("mojin-join-[a-fA-F0-9]{32}")) throw new IOException("Invalid join pipe");
        if (!pipe.equals(launcherPipe)) throw new IOException("Launcher pipe changed after initialization");
        final RandomAccessFile[] connection = new RandomAccessFile[1];
        Future<String> result = workers.submit(new Callable<String>() {
            public String call() throws Exception {
                String stage = "pipe_open";
                try (RandomAccessFile f = openPipe(pipe)) {
                    connection[0] = f;
                    stage = "pipe_write";
                    f.write(("{\"instance\":" + quote(instance) + "}\n").getBytes(StandardCharsets.UTF_8));
                    stage = "pipe_read";
                    ByteArrayOutputStream response = new ByteArrayOutputStream();
                    for (int n = 0; n < 4096; n++) { int b = f.read(); if (b < 0) throw new EOFException(); if (b == '\n') break; response.write(b); }
                    String json = new String(response.toByteArray(), StandardCharsets.UTF_8);
                    stage = "pipe_response";
                    String ticket = jsonString(json, "ticket");
                    if (ticket == null || !ticket.matches("[A-Za-z0-9_-]{43}")) throw new IOException("No join ticket");
                    return ticket;
                } catch (Exception ex) { throw new JoinFailure(stage, ex); }
            }
        });
        try { return result.get(20, TimeUnit.SECONDS); }
        finally { result.cancel(true); if (connection[0] != null) try { connection[0].close(); } catch (IOException ignored) {} }
    }
    private static RandomAccessFile openPipe(String pipe) throws Exception {
        File path = new File("\\\\.\\pipe\\" + pipe);
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
        for (;;) {
            try { return new RandomAccessFile(path, "rw"); }
            catch (FileNotFoundException error) {
                // Java 8 FilePermission canonicalization can briefly connect to a
                // named pipe before RandomAccessFile opens it. Let the launcher
                // discard that empty probe and reaccept the real game request.
                // Retry is bounded and never changes the endpoint or identity.
                if (System.nanoTime() >= deadline) throw error;
                Thread.sleep(25);
            }
        }
    }

    public static boolean read(final Object manager, final Object context, final Object packet) {
        if (!server) return false;
        try {
            String type = packet.getClass().getName();
            boolean handshake = type.endsWith(".C00Handshake") || type.endsWith(".ClientIntentionPacket");
            if (handshake) refreshMode();
            if (mode.equals("off") && !handshake) return false;
            boolean login = type.endsWith(".C00PacketLoginStart") || type.endsWith(".CPacketLoginStart") || type.endsWith(".ServerboundHelloPacket");
            Gate existing = gates.get(manager);
            if (handshake) {
                if (existing != null) return reject(manager, context, "重复握手，请重新连接。", "duplicate_handshake");
                if (!loginIntention(packet)) return false;
                Gate gate = new Gate();
                gate.admitted = mode.equals("off");
                Field field = stringField(packet.getClass()); String host = (String) field.get(packet);
                int start = host.indexOf(MARKER);
                if (start >= 0) {
                    int end = start + MARKER.length() + 43;
                    if (end <= host.length()) {
                        String ticket = host.substring(start + MARKER.length(), end);
                        if (ticket.matches("[A-Za-z0-9_-]{43}") && (end == host.length() || host.charAt(end) == '\0')) {
                            gate.ticket = ticket;
                            field.set(packet, host.substring(0, start) + host.substring(end));
                        }
                    }
                    if (gate.ticket == null) return reject(manager, context, "入服凭据格式错误，请更新统一客户端。", "malformed_ticket");
                }
                gates.put(manager, gate);
                return false;
            }
            if (existing != null && existing.pending) return reject(manager, context, "入服认证尚未完成，请重新连接。", "packet_during_auth");
            if (!login) {
                if (existing != null && !existing.admitted) return reject(manager, context, "请先完成入服认证。", "unexpected_prelogin_packet");
                return false;
            }
            final Gate gate = existing;
            if (gate != null && gate.resumePacket == packet) { gate.resumePacket = null; gate.admitted = true; return false; }
            if (gate == null || gate.loginSeen) return reject(manager, context, "登录顺序错误，请重新连接。", "login_sequence");
            gate.loginSeen = true;
            final String name = loginName(packet);
            if (gate.ticket == null) {
                if (mode.equals("observe")) { log("observe missing_ticket instance=" + instance); gate.admitted = true; return false; }
                return reject(manager, context, "请使用魔金大帅统一客户端启动本服务器。下载：github.com/M4rkzzz/mojin-dashuai", "missing_ticket");
            }
            if (name == null || !name.matches("[A-Za-z0-9_]{1,16}")) return reject(manager, context, "游戏名无效。", "invalid_name");
            gate.pending = true;
            try {
                workers.execute(new Runnable() {
                    public void run() {
                        final boolean allowed = redeem(gate.ticket, name);
                        gate.ticket = null;
                        try { execute(context, new Runnable() {
                            public void run() {
                                gate.pending = false;
                                if (!active(context)) return;
                                if (!allowed && mode.equals("enforce")) { reject(manager, context, "入服认证失败或凭据失效，请回统一客户端重新登录后重连。", "redeem_denied"); return; }
                                if (!allowed) log("observe redeem_denied instance=" + instance);
                                gate.resumePacket = packet;
                                try { dispatch(manager, context, packet); }
                                catch (Exception e) { reject(manager, context, "入服认证处理失败，请重新连接。", "dispatch_failed"); }
                            }
                        }); } catch (Exception ignored) { close(context); }
                    }
                });
            } catch (RejectedExecutionException e) { gate.pending = false; return reject(manager, context, "入服认证繁忙，请稍后重试。", "busy"); }
            return true;
        } catch (Exception e) { return reject(manager, context, "入服认证组件异常，请联系管理员。", "adapter_error"); }
    }

    private static synchronized void refreshMode() {
        File file = new File(configPath);
        long stamp = file.lastModified();
        if (stamp == configStamp) return;
        String updated = "enforce";
        try {
            if (!file.isFile() || file.length() > 65536) throw new IOException("Invalid join config");
            Properties p = new Properties();
            try (Reader reader = new InputStreamReader(new FileInputStream(file), StandardCharsets.UTF_8)) { p.load(reader); }
            String requested = p.getProperty("mode", "").trim();
            if (!Arrays.asList("off", "observe", "enforce").contains(requested)) throw new IOException("Invalid join mode");
            updated = requested;
        } catch (Exception ignored) { log("mode configuration invalid; enforcing authentication"); }
        configStamp = stamp;
        mode = updated;
        log("mode changed " + mode);
    }

    private static boolean redeem(String ticket, String name) {
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(endpoint).openConnection();
            connection.setInstanceFollowRedirects(false); connection.setConnectTimeout(8000); connection.setReadTimeout(8000);
            connection.setRequestMethod("POST"); connection.setDoOutput(true);
            connection.setRequestProperty("Authorization", "Bearer " + secret);
            connection.setRequestProperty("Content-Type", "application/json");
            byte[] body = ("{\"ticket\":" + quote(ticket) + ",\"instance\":" + quote(instance) + ",\"gameName\":" + quote(name) + "}").getBytes(StandardCharsets.UTF_8);
            connection.setFixedLengthStreamingMode(body.length);
            try (OutputStream out = connection.getOutputStream()) { out.write(body); }
            if (connection.getResponseCode() != 200) return false;
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            try (InputStream input = connection.getInputStream()) { byte[] buffer = new byte[1024]; int n; while ((n = input.read(buffer)) >= 0) { out.write(buffer, 0, n); if (out.size() > 8192) return false; } }
            String json = new String(out.toByteArray(), StandardCharsets.UTF_8);
            String expectedUuid = UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(StandardCharsets.UTF_8)).toString().replace("-", "");
            String uuid = jsonString(json, "gameUuid");
            return Pattern.compile("\"allowed\"\\s*:\\s*true(?:\\s*[,}])").matcher(json).find()
                    && name.equals(jsonString(json, "gameName")) && uuid != null && expectedUuid.equalsIgnoreCase(uuid.replace("-", ""));
        } catch (Exception ignored) { return false; }
        finally { if (connection != null) connection.disconnect(); }
    }

    private static boolean loginIntention(Object packet) throws Exception {
        for (Field f : fields(packet.getClass())) if (f.getType().isEnum()) { f.setAccessible(true); Object value = f.get(packet); if (value instanceof Enum && ((Enum<?>)value).name().equals("LOGIN")) return true; }
        return false;
    }
    private static Field stringField(Class<?> type) throws Exception {
        for (Field f : fields(type)) if (!Modifier.isStatic(f.getModifiers()) && f.getType() == String.class) { f.setAccessible(true); return f; }
        throw new NoSuchFieldException("packet string");
    }
    private static List<Field> fields(Class<?> type) { List<Field> result = new ArrayList<Field>(); for (Class<?> c = type; c != null; c = c.getSuperclass()) result.addAll(Arrays.asList(c.getDeclaredFields())); return result; }
    private static String loginName(Object packet) throws Exception {
        for (Field f : fields(packet.getClass())) {
            if (Modifier.isStatic(f.getModifiers())) continue;
            f.setAccessible(true); Object value = f.get(packet);
            if (f.getType() == String.class) return (String)value;
            if (f.getType().getName().equals("com.mojang.authlib.GameProfile") && value != null) return (String)value.getClass().getMethod("getName").invoke(value);
        }
        return null;
    }
    private static void dispatch(Object manager, Object context, Object packet) throws Exception {
        for (Method m : manager.getClass().getDeclaredMethods()) if (m.getReturnType() == Void.TYPE && m.getParameterTypes().length == 2 && m.getParameterTypes()[0].getName().equals("io.netty.channel.ChannelHandlerContext") && m.getParameterTypes()[1] != Object.class && m.getParameterTypes()[1].isInstance(packet)) {
            m.setAccessible(true); m.invoke(manager, context, packet); return;
        }
        throw new NoSuchMethodException("channelRead0");
    }
    private static Object channel(Object context) throws Exception { return call(context, "channel"); }
    private static Object call(Object object, String name) throws Exception { Method m = object.getClass().getMethod(name); m.setAccessible(true); return m.invoke(object); }
    private static void execute(Object context, Runnable action) throws Exception {
        Object loop = call(channel(context), "eventLoop");
        if (loop instanceof Executor) ((Executor)loop).execute(action); else throw new IllegalStateException("No event executor");
    }
    private static boolean active(Object context) { try { return Boolean.TRUE.equals(call(channel(context), "isActive")); } catch (Exception e) { return false; } }
    private static void close(Object context) { try { call(context, "close"); } catch (Exception ignored) {} }
    private static boolean reject(Object manager, Object context, String message, String code) {
        log("rejected instance=" + instance + " code=" + code);
        try {
            for (Field f : fields(manager.getClass())) {
                if (Modifier.isStatic(f.getModifiers())) continue;
                f.setAccessible(true); Object listener = f.get(manager);
                if (listener == null || !(listener.getClass().getName().contains("LoginServer") || listener.getClass().getName().contains("ServerLoginPacketListener"))) continue;
                for (Method m : listener.getClass().getDeclaredMethods()) {
                    if ((m.getName().equals("func_147322_a") || m.getName().equals("func_194026_b") || m.getName().equals("disconnect") || m.getName().equals("m_10053_")) && m.getParameterTypes().length == 1) {
                        m.setAccessible(true); Class<?> parameter = m.getParameterTypes()[0];
                        if (parameter == String.class) { m.invoke(listener, message); return true; }
                        Object component = component(parameter, message, listener.getClass().getClassLoader());
                        if (component != null) { m.invoke(listener, component); return true; }
                    }
                }
            }
        } catch (Exception ignored) {}
        close(context); return true;
    }
    private static Object component(Class<?> parameter, String message, ClassLoader loader) throws Exception {
        if (parameter.getName().equals("net.minecraft.network.chat.Component")) {
            for (Method m : parameter.getMethods()) if (Modifier.isStatic(m.getModifiers()) && (m.getName().equals("literal") || m.getName().equals("m_237113_")) && Arrays.equals(m.getParameterTypes(), new Class<?>[]{String.class})) return m.invoke(null, message);
        }
        for (String name : new String[]{"net.minecraft.util.text.TextComponentString", "net.minecraft.util.ChatComponentText"}) {
            try { Class<?> c = Class.forName(name, true, loader); if (parameter.isAssignableFrom(c)) return c.getConstructor(String.class).newInstance(message); } catch (ClassNotFoundException ignored) {}
        }
        return null;
    }
    public static String quote(String value) { return "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\r", "\\r").replace("\n", "\\n") + "\""; }
    public static String jsonString(String json, String key) {
        Matcher matcher = Pattern.compile("\"" + Pattern.quote(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").matcher(json);
        if (!matcher.find()) return null;
        String raw = matcher.group(1); StringBuilder output = new StringBuilder();
        for (int i = 0; i < raw.length(); i++) { char c = raw.charAt(i); if (c != '\\') { output.append(c); continue; } if (++i >= raw.length()) return null; c = raw.charAt(i); if (c == 'u') { if (i + 4 >= raw.length()) return null; try { output.append((char)Integer.parseInt(raw.substring(i + 1, i + 5), 16)); } catch (NumberFormatException e) { return null; } i += 4; } else if (c == 'n') output.append('\n'); else if (c == 'r') output.append('\r'); else if (c == 't') output.append('\t'); else output.append(c); }
        return output.toString();
    }
    private static void log(String message) { System.out.println("[Mojin Join] " + message); }
}
