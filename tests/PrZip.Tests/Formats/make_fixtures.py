"""Genera archivos de prueba con implementaciones independientes del motor (tarfile, gzip, bz2 y lzma de
Python; bsdtar/libarchive si está) y un manifiesto con lo que cada uno debe contener.

Uso: python -I make_fixtures.py <carpeta_destino> [ruta_bsdtar]
"""
import bz2
import gzip
import hashlib
import io
import json
import lzma
import os
import subprocess
import sys
import tarfile
import time

out = sys.argv[1]
bsdtar = sys.argv[2] if len(sys.argv) > 2 else None
os.makedirs(out, exist_ok=True)
manifest = {}
MTIME = 1_700_000_000


def sha(data):
    return hashlib.sha256(data).hexdigest()


# --- Contenido de referencia ------------------------------------------------------------------------
files = {
    "acta.txt": ("renglón del acta, ñandú y 漢字\n" * 400).encode("utf-8"),
    "datos/binario.bin": bytes((i * 31 + 7) % 256 for i in range(70_000)),
    "datos/vacio.txt": b"",
    "datos/sub/ceros.bin": bytes(300_000),
    "datos/" + "nombre_muy_largo_" * 8 + ".txt": b"nombre largo de mas de 100 caracteres\n",
}


def expected_entries(extra=()):
    entries = [{"name": n, "kind": "File", "sha256": sha(d)} for n, d in files.items()]
    dirs = sorted({os.path.dirname(n) for n in files if "/" in n} | {"datos/sub", "vacia"})
    entries += [{"name": d, "kind": "Directory"} for d in dirs]
    return entries + list(extra)


# --- TAR con tarfile (GNU, pax, ustar) con enlaces, dispositivo y FIFO ---------------------------
def make_tar(fmt):
    bio = io.BytesIO()
    with tarfile.open(fileobj=bio, mode="w", format=fmt) as tar:
        for d in ["datos", "datos/sub", "vacia"]:
            ti = tarfile.TarInfo(d)
            ti.type = tarfile.DIRTYPE
            ti.mtime = MTIME
            tar.addfile(ti)
        for name, data in files.items():
            if fmt == tarfile.USTAR_FORMAT and len(name) > 100 and "/" not in name[-100:]:
                continue
            ti = tarfile.TarInfo(name)
            ti.size = len(data)
            ti.mtime = MTIME
            tar.addfile(ti, io.BytesIO(data))
        for name, typ, target in [("enlace_simbolico", tarfile.SYMTYPE, "acta.txt"),
                                  ("enlace_duro", tarfile.LNKTYPE, "acta.txt"),
                                  ("dispositivo", tarfile.CHRTYPE, None),
                                  ("tuberia", tarfile.FIFOTYPE, None)]:
            ti = tarfile.TarInfo(name)
            ti.type = typ
            ti.mtime = MTIME
            if target:
                ti.linkname = target
            tar.addfile(ti)
    return bio.getvalue()


tar_extra = [
    {"name": "enlace_simbolico", "kind": "SymbolicLink", "link": "acta.txt"},
    {"name": "enlace_duro", "kind": "HardLink", "link": "acta.txt"},
    {"name": "dispositivo", "kind": "Other"},
    {"name": "tuberia", "kind": "Other"},
]

for label, fmt in [("gnu", tarfile.GNU_FORMAT), ("pax", tarfile.PAX_FORMAT)]:
    data = make_tar(fmt)
    variants = {
        f"py_{label}.tar": data,
        f"py_{label}.tar.gz": gzip.compress(data, mtime=0),
        f"py_{label}.tar.bz2": bz2.compress(data),
        f"py_{label}.tar.xz": lzma.compress(data, check=lzma.CHECK_CRC64),
    }
    for fname, blob in variants.items():
        with open(os.path.join(out, fname), "wb") as f:
            f.write(blob)
        fmt_name = "tar" if fname.endswith(".tar") else "tar." + fname.rsplit(".", 1)[1]
        manifest[fname] = {"format": fmt_name, "entries": expected_entries(tar_extra)}

# --- TAR con "./" adelante y la entrada raíz "./", como deja "tar -c ." -------------------------------
bio = io.BytesIO()
with tarfile.open(fileobj=bio, mode="w", format=tarfile.PAX_FORMAT) as tar:
    for name, data in [("./", None), ("./a.txt", b"uno"), ("./d/", None), ("./d/b.txt", b"dos")]:
        ti = tarfile.TarInfo(name)
        ti.mtime = MTIME
        if data is None:
            ti.type = tarfile.DIRTYPE
            tar.addfile(ti)
        else:
            ti.size = len(data)
            tar.addfile(ti, io.BytesIO(data))
with open(os.path.join(out, "punto_barra.tar"), "wb") as f:
    f.write(bio.getvalue())
manifest["punto_barra.tar"] = {"format": "tar", "exact": True, "entries": [
    {"name": "a.txt", "kind": "File", "sha256": sha(b"uno")},
    {"name": "d", "kind": "Directory"},
    {"name": "d/b.txt", "kind": "File", "sha256": sha(b"dos")}]}

# --- Archivos sueltos comprimidos ------------------------------------------------------------------
single = files["acta.txt"] * 30
blob = io.BytesIO()
with gzip.GzipFile(filename="declaracion_original.txt", mode="wb", fileobj=blob, mtime=MTIME) as g:
    g.write(single[: len(single) // 2])
with gzip.GzipFile(filename="ignorado.txt", mode="wb", fileobj=blob, mtime=MTIME) as g:  # segundo miembro
    g.write(single[len(single) // 2:])
with open(os.path.join(out, "suelto.gz"), "wb") as f:
    f.write(blob.getvalue())
manifest["suelto.gz"] = {"format": "gz", "entries": [{"name": "declaracion_original.txt", "kind": "File", "sha256": sha(single)}]}

with open(os.path.join(out, "suelto.txt.bz2"), "wb") as f:
    f.write(bz2.compress(single[:100_000]) + bz2.compress(single[100_000:]))  # dos streams
manifest["suelto.txt.bz2"] = {"format": "bz2", "entries": [{"name": "suelto.txt", "kind": "File", "sha256": sha(single)}]}

# XZ: los cuatro tipos de verificación, filtros y streams concatenados con relleno.
xz_cases = {
    "check_none.bin.xz": dict(check=lzma.CHECK_NONE),
    "check_crc32.bin.xz": dict(check=lzma.CHECK_CRC32),
    "check_crc64.bin.xz": dict(check=lzma.CHECK_CRC64),
    "check_sha256.bin.xz": dict(check=lzma.CHECK_SHA256),
    "filtro_x86.bin.xz": dict(check=lzma.CHECK_CRC32, filters=[{"id": lzma.FILTER_X86}, {"id": lzma.FILTER_LZMA2, "preset": 6}]),
    "filtro_delta.bin.xz": dict(check=lzma.CHECK_CRC32, filters=[{"id": lzma.FILTER_DELTA, "dist": 4}, {"id": lzma.FILTER_LZMA2}]),
}
payload = files["datos/binario.bin"] * 8 + files["acta.txt"] * 10
for fname, kw in xz_cases.items():
    with open(os.path.join(out, fname), "wb") as f:
        f.write(lzma.compress(payload, format=lzma.FORMAT_XZ, **kw))
    manifest[fname] = {"format": "xz", "entries": [{"name": fname[:-3], "kind": "File", "sha256": sha(payload)}]}

concat = lzma.compress(payload[:50_000]) + bytes(8) + lzma.compress(payload[50_000:], check=lzma.CHECK_SHA256)
with open(os.path.join(out, "concatenado.bin.xz"), "wb") as f:
    f.write(concat)
manifest["concatenado.bin.xz"] = {"format": "xz", "entries": [{"name": "concatenado.bin", "kind": "File", "sha256": sha(payload)}]}

# --- bsdtar (libarchive): otro productor de TAR y el de ISO ---------------------------------------
if bsdtar:
    src = os.path.join(out, "_src")
    for name, data in files.items():
        p = os.path.join(src, name)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)
        os.utime(p, (MTIME, MTIME))
    os.makedirs(os.path.join(src, "vacia"), exist_ok=True)
    os.makedirs(os.path.join(src, "datos", "sub"), exist_ok=True)

    def run(args):
        subprocess.run([bsdtar] + args, cwd=src, check=True, capture_output=True)

    for fmt in ["ustar", "pax", "gnutar", "v7"]:
        # v7 y ustar no admiten el nombre de más de 100 caracteres: bsdtar lo omite con un aviso.
        names = [n for n in os.listdir(src)]
        try:
            run(["--format", fmt, "-cf", os.path.join(out, f"bsd_{fmt}.tar")] + names)
        except subprocess.CalledProcessError:
            pass
        if os.path.exists(os.path.join(out, f"bsd_{fmt}.tar")):
            entries = expected_entries()
            if fmt in ("ustar", "v7"):
                entries = [e for e in entries if "nombre_muy_largo" not in e["name"]]
            manifest[f"bsd_{fmt}.tar"] = {"format": "tar", "entries": entries, "partial": fmt in ("ustar", "v7")}

    for flag, ext in [("-z", "tgz"), ("-j", "tar.bz2"), ("-J", "tar.xz")]:
        name = f"bsd.{ext}"
        run(["--format", "pax", flag, "-cf", os.path.join(out, name)] + os.listdir(src))
        manifest[name] = {"format": {"tgz": "tar.gz", "tar.bz2": "tar.bz2", "tar.xz": "tar.xz"}[ext], "entries": expected_entries()}

    run(["--format", "iso9660", "-cf", os.path.join(out, "bsd_joliet.iso")] + os.listdir(src))
    manifest["bsd_joliet.iso"] = {"format": "iso (Joliet)", "entries": expected_entries(), "names_ci": False}

json.dump(manifest, open(os.path.join(out, "manifest.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(len(manifest), "archivos")
