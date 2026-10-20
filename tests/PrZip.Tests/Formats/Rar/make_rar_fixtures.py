"""Genera los archivos RAR de prueba con los Rar.exe oficiales de RARLAB y un manifiesto con lo que cada
uno debe contener. Los .rar quedan versionados en esta carpeta: Rar.exe no está en todas las máquinas.

Uso: python -I make_rar_fixtures.py <Rar.exe 7.x> <Rar.exe 6.x> <Rar.exe 2.90>

- RAR 7.01 crea RAR5 (y RAR7). Desde WinRAR 7 ya no crea RAR4 (-ma4 no existe).
- RAR 6.24 (winrar-x64-624.exe de rarlab.com, firmado por win.rar GmbH; se extrae Rar.exe con 7-Zip sin
  instalar) crea RAR4 con -ma4.
- RAR 2.90 (wrar290.exe de rarlab.com, 2001, sin firma digital; Rar.exe de consola Win32 extraído con 7-Zip)
  crea el formato 2.0 (algoritmo v20), con el modo multimedia -mm. RAR 1.5 y 1.4 (DOS), el filtro ARM de
  RAR5 y el Itanium de RAR 3.x salen de make_rar_fixtures_viejos.py, que se suma a manifest.json.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

rar7, rar6, rar2 = sys.argv[1], sys.argv[2], sys.argv[3]
here = os.path.dirname(os.path.abspath(__file__))
MTIME = 1_700_000_000


def sha(data):
    return hashlib.sha256(data).hexdigest()


# Contenido de referencia: texto que comprime bien, binario pseudoaleatorio, ceros, un vacío, nombres
# Unicode y una carpeta vacía.
files = {
    "acta.txt": ("renglón del acta, ñandú y 漢字\n" * 300).encode("utf-8"),
    "datos/binario.bin": bytes((i * 2654435761 >> 13) & 0xFF for i in range(40_000)),
    "datos/vacio.txt": b"",
    "datos/sub/ceros.bin": bytes(30_000),
    "ñandú 漢字.txt": "nombre con acentos y kanji\n".encode("utf-8"),
}
empty_dirs = ["vacía"]


def lcg_bytes(n, seed):
    out = bytearray(n)
    x = seed
    for i in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out[i] = x >> 16 & 0xFF
    return bytes(out)


def x86_like(n):
    # Código x86 sintético: muchas llamadas E8/saltos E9 con destinos relativos, para que RAR elija el
    # filtro E8/E8E9.
    # filtro E8/E8E9. Las llamadas van a unas pocas funciones (direcciones absolutas repetidas): es lo que
    # el filtro aprovecha al convertir los desplazamientos relativos en absolutos.
    out = bytearray()
    ops = [bytes.fromhex(h) for h in ("558bec", "8b4508", "8945fc", "83c404", "5dc3", "50", "6a00")]
    rnd = lcg_bytes(n, 7)
    i = 0
    while len(out) < n:
        out += ops[rnd[i] % len(ops)] + ops[rnd[i + 1] % len(ops)]
        target = 0x1000 + (rnd[i + 2] % 40) * 0x80
        rel = target - (len(out) + 5)
        out += bytes([0xE8 if rnd[i + 3] % 4 else 0xE9]) + (rel & 0xFFFFFFFF).to_bytes(4, "little")
        i += 4
    return bytes(out[:n])


def audio_like(n):
    # Audio PCM de 16 bits estéreo sintético (dos senoidales): para el filtro delta.
    import math
    out = bytearray()
    t = 0
    while len(out) < n:
        left = int(12000 * math.sin(t / 23.0))
        right = int(9000 * math.sin(t / 31.0 + 1))
        out += left.to_bytes(2, "little", signed=True) + right.to_bytes(2, "little", signed=True)
        t += 1
    return bytes(out[:n])


def words_text(n):
    vocab = "acta perito evidencia cadena custodia hash sello archivo carpeta juzgado causa expediente "             "fecha hora disco imagen copia forense registro informe".split()
    rnd = lcg_bytes(n, 99)
    out = []
    size = 0
    i = 0
    while size < n:
        w = vocab[rnd[i % len(rnd)] % len(vocab)]
        out.append(w)
        size += len(w) + 1
        i += 1
    return (" ".join(out)).encode("ascii")[:n]


# Para los métodos comprimidos se suman archivos que ejercitan los filtros y el giro de la ventana.
files_c = dict(files)
files_c.update({
    "datos/programa.exe": x86_like(200_000),
    "datos/audio.raw": audio_like(120_000),
    "datos/largo.txt": words_text(300_000),
})


# RAR 2.0 no guarda nombres Unicode (usa la página OEM de quien lo crea): para esos, solo nombres ASCII.
files_c20 = {n: d for n, d in files_c.items() if n.isascii()}


def expected_entries(fileset):
    entries = [{"name": n, "kind": "File", "sha256": sha(d)} for n, d in fileset.items()]
    dirs = sorted({os.path.dirname(n) for n in fileset if "/" in n} | set(empty_dirs))
    entries += [{"name": d, "kind": "Directory"} for d in dirs]
    return sorted(entries, key=lambda e: e["name"])


def build_tree(root, fileset):
    for name, data in fileset.items():
        path = os.path.join(root, *name.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
    for d in empty_dirs:
        os.makedirs(os.path.join(root, d), exist_ok=True)
    for dirpath, dirnames, filenames in os.walk(root):
        for n in filenames + dirnames:
            os.utime(os.path.join(dirpath, n), (MTIME, MTIME))


# nombre del fixture -> (Rar.exe, interruptores, contenido)
variants = {
    "m0_v5.rar": (rar7, ["-ma5", "-m0"], files),
    "m0_v5_blake2.rar": (rar7, ["-ma5", "-m0", "-htb"], files),
    "m0_v4.rar": (rar6, ["-ma4", "-m0"], files),
    "m1_v5.rar": (rar7, ["-ma5", "-m1"], files_c),
    "m3_v5.rar": (rar7, ["-ma5", "-m3"], files_c),
    "m5_v5.rar": (rar7, ["-ma5", "-m5"], files_c),
    "m5_v5_solid.rar": (rar7, ["-ma5", "-m5", "-s"], files_c),
    "m5_v5_md128k.rar": (rar7, ["-ma5", "-m5", "-md128k"], files_c),
    "m5_v5_solid_md128k_blake2.rar": (rar7, ["-ma5", "-m5", "-s", "-md128k", "-htb"], files_c),
    # RAR4 (algoritmo v29): métodos, sólido, diccionario chico, PPMd forzado (-mct+) y cada filtro
    # estándar de la RarVM forzado (-mca+ audio, -mcc+ color, -mce+ x86, -mc4d+ delta). El de Itanium no:
    # Rar 6 ignora -mci+ con este contenido y el archivo sale idéntico a -m3.
    "m1_v4.rar": (rar6, ["-ma4", "-m1"], files_c),
    "m3_v4.rar": (rar6, ["-ma4", "-m3"], files_c),
    "m5_v4_solid.rar": (rar6, ["-ma4", "-m5", "-s"], files_c),
    "m3_v4_md64k.rar": (rar6, ["-ma4", "-m3", "-md64k"], files_c),
    "m3_v4_ppmd.rar": (rar6, ["-ma4", "-m3", "-mct+"], files_c),
    "m5_v4_solid_ppmd.rar": (rar6, ["-ma4", "-m5", "-s", "-mct+"], files_c),
    "m3_v4_audio.rar": (rar6, ["-ma4", "-m3", "-mca+"], files_c),
    "m3_v4_rgb.rar": (rar6, ["-ma4", "-m3", "-mcc+"], files_c),
    "m3_v4_x86.rar": (rar6, ["-ma4", "-m3", "-mce+"], files_c),
    "m3_v4_delta.rar": (rar6, ["-ma4", "-m3", "-mc4d+"], files_c),
    # RAR 2.0 (v20) con Rar 2.90: normal, sólido, diccionario de 64 KB y multimedia forzado (audio de 2.0).
    "m3_v20.rar": (rar2, ["-m3"], files_c20),
    "m5_v20_solid.rar": (rar2, ["-m5", "-s"], files_c20),
    "m3_v20_md64.rar": (rar2, ["-m3", "-md64"], files_c20),
    "m3_v20_mmf.rar": (rar2, ["-m3", "-mmf"], files_c20),
}

# Cifrados: nombre -> (Rar.exe, interruptores, contenido, contraseña). PWD_LARGA pasa de 28 caracteres para
# que la derivación de RAR 3.x reescriba bloques completos (sha1_process_rar29); PWD_ACENTOS prueba la
# conversión de la contraseña (UTF-8 en RAR5, UTF-16 en RAR3, ANSI en RAR 2.0).
PWD = "Clave123"
PWD_LARGA = "una contraseña larga de más de treinta caracteres"
PWD_ACENTOS = "ñandú-Señal"
encrypted = {
    "m3_v5_pass.rar": (rar7, ["-ma5", "-m3"], files_c, PWD),
    "m3_v5_hpass.rar": (rar7, ["-ma5", "-m3"], files_c, PWD),
    "m5_v5_solid_hpass_acentos.rar": (rar7, ["-ma5", "-m5", "-s"], files_c, PWD_ACENTOS),
    "m0_v5_pass_blake2.rar": (rar7, ["-ma5", "-m0", "-htb"], files, PWD),
    "m3_v4_pass.rar": (rar6, ["-ma4", "-m3"], files_c, PWD),
    "m3_v4_hpass_larga.rar": (rar6, ["-ma4", "-m3"], files_c, PWD_LARGA),
    "m5_v4_solid_pass_acentos.rar": (rar6, ["-ma4", "-m5", "-s", "-mct+"], files_c, PWD_ACENTOS),
    "m3_v20_pass.rar": (rar2, ["-m3"], files_c20, PWD),
}

# Autoextraíble: el módulo SFX real es un binario de RARLAB que no se versiona aquí. Para probar la búsqueda
# de la firma alcanza un prefijo "MZ" sintético (sin la firma adentro) delante de un RAR común.
SFX_STUB = b"MZ" + bytes((i * 7 + 3) % 251 for i in range(70_000))
sfx_variants = {"m0_v5_sfx.exe": "m0_v5.rar"}

manifest = {}
with tempfile.TemporaryDirectory() as tmp:
    trees = {}
    for name, (exe, switches, fileset) in variants.items():
        key = id(fileset)
        if key not in trees:
            trees[key] = os.path.join(tmp, f"src{len(trees)}")
            os.makedirs(trees[key])
            build_tree(trees[key], fileset)
        out = os.path.join(tmp, name)
        # -r recursivo, -idq silencioso, -y sin preguntas.
        quiet = "-inul" if exe == rar2 else "-idq"  # RAR 2.90 no conoce -idq
        cmd = [exe, "a", "-r", quiet, "-y"] + switches + [out, "*"]
        subprocess.run(cmd, cwd=trees[key], check=True)
        shutil.copyfile(out, os.path.join(here, name))
        manifest[name] = {"command": " ".join(["rar"] + cmd[1:-2] + [name, "*"]), "entries": expected_entries(fileset)}

    for name, (exe, switches, fileset, pwd) in encrypted.items():
        key = id(fileset)
        if key not in trees:
            trees[key] = os.path.join(tmp, f"src{len(trees)}")
            os.makedirs(trees[key])
            build_tree(trees[key], fileset)
        out = os.path.join(tmp, name)
        quiet = "-inul" if exe == rar2 else "-idq"
        flag = ("-hp" if "hpass" in name else "-p") + pwd
        cmd = [exe, "a", "-r", quiet, "-y"] + switches + [flag, out, "*"]
        subprocess.run(cmd, cwd=trees[key], check=True)
        shutil.copyfile(out, os.path.join(here, name))
        shown = " ".join(["rar"] + cmd[1:-3] + [flag[:3] + "<contraseña>", name, "*"])
        manifest[name] = {"command": shown, "password": pwd, "entries": expected_entries(fileset)}

    # Volúmenes: nombre base -> (Rar.exe, interruptores, contenido, contraseña o None). Se versionan todos
    # los volúmenes; el manifiesto va con el nombre del primero.
    volumes = {
        "vol_v5": (rar7, ["-ma5", "-m3", "-v40k"], files_c, None),
        "vol_v5_solid": (rar7, ["-ma5", "-m5", "-s", "-v40k"], files_c, None),
        "vol_v5_hpass": (rar7, ["-ma5", "-m3", "-v40k"], files_c, PWD),
        "vol_v4": (rar6, ["-ma4", "-m3", "-v40k"], files_c, None),
        "vol_v4_viejo": (rar6, ["-ma4", "-m3", "-v40k", "-vn"], files_c, None),
        "vol_v4_pass": (rar6, ["-ma4", "-m3", "-v40k"], files_c, PWD),
    }
    for base, (exe, switches, fileset, pwd) in volumes.items():
        key = id(fileset)
        if key not in trees:
            trees[key] = os.path.join(tmp, f"src{len(trees)}")
            os.makedirs(trees[key])
            build_tree(trees[key], fileset)
        outdir = os.path.join(tmp, "v_" + base)
        os.makedirs(outdir)
        extra = [("-hp" if "hpass" in base else "-p") + pwd] if pwd else []
        cmd = [exe, "a", "-r", "-idq", "-y"] + switches + extra + [os.path.join(outdir, base + ".rar"), "*"]
        subprocess.run(cmd, cwd=trees[key], check=True)
        parts = sorted(os.listdir(outdir))
        for part in parts:
            shutil.copyfile(os.path.join(outdir, part), os.path.join(here, part))
        # Rar numera part1/part01 según cuántas partes salen; con -vn el primero es .rar y siguen .r00, .r01…
        first = base + ".rar" if "viejo" in base else parts[0]
        assert first in parts, parts
        entry = {"command": " ".join(["rar"] + cmd[1:-2] + [base + ".rar", "*"]).replace(pwd, "<contraseña>") if pwd else " ".join(["rar"] + cmd[1:-2] + [base + ".rar", "*"]),
                 "volumes": parts, "entries": expected_entries(fileset)}
        if pwd:
            entry["password"] = pwd
        manifest[first] = entry

for name, base in sfx_variants.items():
    assert b"Rar!" not in SFX_STUB
    with open(os.path.join(here, base), "rb") as f:
        data = f.read()
    with open(os.path.join(here, name), "wb") as f:
        f.write(SFX_STUB + data)
    manifest[name] = {"command": f"prefijo MZ de {len(SFX_STUB)} bytes + {base}", "entries": expected_entries(files)}

with open(os.path.join(here, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=1)
print("ok:", ", ".join(manifest))
