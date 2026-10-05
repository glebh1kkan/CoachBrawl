// redirect.js — frida-скрипт для CoachBrawl.apk (53.170 + frida-gadget).
// запуск на ПК: frida -R -l redirect.js -n brawlstars  (после adb forward)
// перехватывает TCP-коннект игры (порт 9339) и ведёт его на наш сервер.
// UDP-бои трогать не надо: адрес/порт UDP клиент получает от сервера.
const SERVER_IP = "150.241.70.48";
const GAME_TCP_PORT = 9339;

function ipv4Of(sockaddr) {
    return sockaddr.add(4).readByteArray(4);
}

const connectPtr = Module.getExportByName("libc.so", "connect");
Interceptor.attach(connectPtr, {
    onEnter(args) {
        try {
            const family = args[1].readU16();
            if (family !== 2) return; // только AF_INET
            const port = (args[1].add(2).readU8() << 8) | args[1].add(3).readU8();
            if (port !== GAME_TCP_PORT) return;
            const orig = [0, 1, 2, 3].map(i => args[1].add(4 + i).readU8() & 0xff).join(".");
            const parts = SERVER_IP.split(".").map(Number);
            for (let i = 0; i < 4; i++) args[1].add(4 + i).writeU8(parts[i]);
            console.log("[coach] tcp " + orig + ":" + port + " -> " + SERVER_IP + ":" + port);
        } catch (e) {
            console.log("[coach] connect hook err: " + e);
        }
    }
});
console.log("[coach] redirect loaded: tcp 9339 -> " + SERVER_IP);
