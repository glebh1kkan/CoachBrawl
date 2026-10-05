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

## v4: база — оригинальный ShuzaBrawl.apk (рекомендуемый путь)

оригинал: пакет `com.sh.shuzybrawl`, версия `53.007` (совпадает с сервером),
frida-мод `libindusbrawl.so` + скрипт `libindusbrawl.script.so` (arm64) /
конфиг `libindusbrawl.c.so` + скрипт `libindusbrawl.s.so` (arm).
скрипты вели на `148.113.240.165:9339`.

патч под coachbrawl (без apktool — архив у модеров битый для него):
1. `7z x ShuzaBrawl.apk` (7z терпит битые заголовки, unzip — нет)
2. в `lib/arm64-v8a/libindusbrawl.script.so`: `148.113.240.165` → `150.241.70.48`,
   `Telegram: @ShuzaBrawl` → `Telegram: @CoachBrawl`
3. в `lib/armeabi-v7a/libindusbrawl.c.so` (json): ip → наш
4. в `lib/armeabi-v7a/libindusbrawl.s.so`: `@shuzabrawl` → `@coachbrawl`
5. манифест: пересборка string pool (utf8) — пакет и провайдеры
   `com.sh.shuzybrawl` → `com.coachbrawl.brawl`, label `ShuzaBrawl` → `CoachBrawl`,
   класс `com.supercell.brawlstars.GameApp` не трогаем
6. удалить старые подписи `META-INF/*.SF|*.RSA|*.MF`, `zip -qr -1`, подпись apksigner

итог: пакет `com.coachbrawl.brawl`, версия `53.007`, редирект не нужен —
клиент сам идёт на `150.241.70.48:9339`. просто скачать и играть.
важно при пересборке zip: нативные `.so` паковать БЕЗ сжатия (`zip -n .so`), иначе android 7+ не ставит (native libs must be stored).
