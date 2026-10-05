#!/usr/bin/env python3
# rebuild.py — точная пересборка base-client.apk через central directory
# (локальные заголовки в оригинале рвутся на 781М — идём по central).
# нетронутые записи копируются побайтово, меняются только заданные файлы.
import struct, zlib

def zdec(raw):
    return zlib.decompressobj(-15).decompress(raw)

def zenc(data, level=6):
    c = zlib.compressobj(level, zlib.DEFLATED, -15)
    return c.compress(data) + c.flush()

SRC = "/root/apks/base-client.apk"
DST = "/root/apks/CoachBrawl-test-unsigned.apk"

OLD_IP = b"148.113.240.165"
NEW_IP = b"150.241.70.48"

PATCH_FILES = {
    "lib/arm64-v8a/libindusbrawl.script.so": [(OLD_IP, NEW_IP), (b"@ShuzaBrawl", b"@CoachBrawl")],
    "lib/armeabi-v7a/libindusbrawl.c.so": [(OLD_IP, NEW_IP)],
    "lib/armeabi-v7a/libindusbrawl.s.so": [(b"@shuzabrawl", b"@coachbrawl")],
    # манифест: только замены равной длины (структура pool не меняется)
    "AndroidManifest.xml": [(b"com.sh.shuzybrawl", b"com.coachbrawlapp"), (b"ShuzaBrawl", b"CoachBrawl")],
}

d = open(SRC, "rb").read()
eocd = d.rfind(b"\x50\x4b\x05\x06")
total = struct.unpack("<H", d[eocd + 10:eocd + 12])[0]
cdoff = struct.unpack("<I", d[eocd + 16:eocd + 20])[0]

entries = []
p = cdoff
for _ in range(total):
    assert d[p:p + 4] == b"\x50\x4b\x01\x02", hex(p)
    vmade, ver, flag, method, mtime, mdate = struct.unpack("<HHHHHH", d[p + 4:p + 16])
    crc, cs, us = struct.unpack("<III", d[p + 16:p + 28])
    nlen, elen, clen = struct.unpack("<HHH", d[p + 28:p + 34])
    disk, iattr = struct.unpack("<HH", d[p + 34:p + 38])
    eattr, lho = struct.unpack("<II", d[p + 38:p + 46])
    name = d[p + 46:p + 46 + nlen]
    extra = d[p + 46 + nlen:p + 46 + nlen + elen]
    comment = d[p + 46 + nlen + elen:p + 46 + nlen + elen + clen]
    entries.append(dict(vmade=vmade, ver=ver, flag=flag, method=method, mtime=mtime, mdate=mdate,
                        crc=crc, cs=cs, us=us, name=name, extra=extra, comment=comment,
                        disk=disk, iattr=iattr, eattr=eattr, lho=lho))
    p += 46 + nlen + elen + clen

out = bytearray()
central = bytearray()
changed = 0
import os
FALLBACK = "/root/apks/full7"
dropped = []
for e in entries:
    name_s = e["name"].decode("utf-8")
    lho = e["lho"]
    if d[lho:lho + 4] == b"\x50\x4b\x03\x04":
        lver, lflag, lmethod, ltime, ldate, lcrc, lcs, lus, lnlen, lelen = struct.unpack(
            "<HHHHHIIIHH", d[lho + 4:lho + 30])
        lname = d[lho + 30:lho + 30 + lnlen]
        lextra = d[lho + 30 + lnlen:lho + 30 + lnlen + lelen]
        raw = d[lho + 30 + lnlen + lelen:lho + 30 + lnlen + lelen + e["cs"]]
    else:
        # битая запись оригинала — берём best-effort байты из 7z-распаковки
        fb = os.path.join(FALLBACK, name_s)
        if not os.path.isfile(fb):
            dropped.append(name_s)
            continue
        data = open(fb, "rb").read()
        lver, lflag, lmethod, ltime, ldate = e["ver"], e["flag"], e["method"], e["mtime"], e["mdate"]
        lnlen, lelen = len(e["name"]), 0
        lname, lextra = e["name"], b""
        raw = zenc(data) if e["method"] == 8 else data
    new_raw, crc, cs, us = raw, e["crc"], e["cs"], e["us"]
    if name_s in PATCH_FILES:
        data = zdec(raw) if e["method"] == 8 else raw
        for a, b in PATCH_FILES[name_s]:
            assert a in data, (name_s, a)
            data = data.replace(a, b)
        new_raw = zenc(data) if e["method"] == 8 else data
        crc = zlib.crc32(data) & 0xFFFFFFFF
        cs, us = len(new_raw), len(data)
        changed += 1
    new_lho = len(out)
    nb = e["name"]
    out += struct.pack("<IHHHHHIIIHH", 0x04034B50, lver, lflag, lmethod, ltime, ldate,
                       crc, cs, us, len(nb), len(lextra))
    out += nb + lextra + new_raw
    central += struct.pack("<IHHHHHHIIIHHHHHII", 0x02014B50, e["vmade"], e["ver"], e["flag"],
                           e["method"], e["mtime"], e["mdate"], crc, cs, us,
                           len(nb), len(e["extra"]), len(e["comment"]),
                           e["disk"], e["iattr"], e["eattr"], new_lho)
    central += nb + e["extra"] + e["comment"]

print(f"entries: {len(entries)}, changed: {changed}")
cd_off = len(out)
out += bytes(central)
out += struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, len(entries), len(entries),
                   len(out) - cd_off, cd_off, 0)
open(DST, "wb").write(out)
print(f"wrote {DST} ({len(out)} bytes)")

print("dropped:", len(dropped))
for n in dropped[:20]: print("  -", n)
