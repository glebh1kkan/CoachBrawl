# CoachBrawl.apk — сборка из стока 53.170

исходник: `Brawl_Stars-53.170.apk` (com.supercell.brawlstars, versionCode 53170).
пакет сменён на `com.coachbrawl.brawl` (правится string pool бинарного манифеста, класс `GameApp` не трогаем) — ставится рядом со стоком без конфликта.
клиент стоковый, хост сервера в нём зашифрован, поэтому редирект через frida-gadget.

## что внутри CoachBrawl.apk

- `lib/arm64-v8a/libfrida-gadget.so`, `lib/armeabi-v7a/libfrida-gadget.so` (frida 17.22.2)
- `com.supercell.brawlstars.GameApp.<clinit>` грузит `frida-gadget` при старте
- пакет и подпись свои (ключ `apk_patch/coach.keystore` НЕ лежит в репо — только локально)

сервер принимает версии `53.007 / 53.170 / 53.176` (см. `MessageManager.LoginReceived`).

## пересборка (одна команда, нужен java + apktool.jar в /tmp)

```bash
cd /root/apks/work
java -Xmx6g -jar /tmp/apktool.jar b coachapk -o coach-unsigned.apk
apksigner sign --ks coach.keystore --ks-pass pass:coachbrawl --key-pass pass:coachbrawl \
  --out CoachBrawl.apk coach-unsigned.apk
cp CoachBrawl.apk /root/CoachBrawl/run/wwwroot/
```

`coachapk/` — декомпилятор стока 53.170 (`apktool d -r`) + патч выше.
оригинальный сток-АПК в репо не кладём (979М, `*.apk` в `.gitignore`).

## запуск игры (на телефоне + ПК в одном wifi)

1. установи `CoachBrawl.apk` (скачать: `http://150.241.70.48:8085/CoachBrawl.apk`)
2. включи отладку по usb, подключи к ПК: `adb forward tcp:27042 tcp:27042`
3. на ПК: `pip install frida-tools`, потом `frida -R -l apk_patch/redirect.js -n brawlstars`
4. открой игру — в консоли увидишь `[coach] tcp ...:9339 -> 150.241.70.48:9339`
5. без запущенной frida с этим скриптом игра пойдёт на сервера supercell (редиректа нет)

udp-бои отдельно хукать не надо: адрес udp клиент берёт из сообщения сервера.

## сборка coach_redirect (нужен android-ndk, проверено на r29)

```bash
export PATH=$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin:$PATH
aarch64-linux-android24-clang -shared -fPIC -O2 -o libcoach_redirect_arm64.so coach_redirect.c -llog -ldl
armv7a-linux-androideabi24-clang -shared -fPIC -O2 -o libcoach_redirect_arm.so coach_redirect.c -llog -ldl
```
