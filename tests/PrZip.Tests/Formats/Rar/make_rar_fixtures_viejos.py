"""Genera los fixtures RAR que make_rar_fixtures.py no puede crear con los Rar.exe actuales y los suma a
manifest.json (sin tocar los demás): RAR 1.5 y 1.4 de DOS, sus cifrados, el filtro ARM de RAR5 y el filtro
Itanium de RAR 3.x. Cada archivo creado se contrasta además con UnRAR antes de versionarlo.

Uso: python -I make_rar_fixtures_viejos.py <msdos.exe> <RAR 1.39> <RAR 1.55> <Rar.exe 5.50> <Rar.exe 3.93> <UnRAR.exe>

- msdos.exe: MS-DOS Player de Takeda Toshiya (takeda-toshiya.my.coocan.jp/msdos, binario i486_x64), corre
  programas de consola de DOS en Windows de 64 bits.
- RAR 1.39 beta (19/01/1994) y RAR 1.55 (16/08/1995) para DOS: no están en rarlab.com; se tomaron de
  old-dos.ru (archivo de software de DOS, file_696), dentro de sus distribuciones originales. RAR 1.39 crea
  el formato 1.4 (firma "RE~^", cifrado de RAR 1.3) y RAR 1.55 el 1.5 (algoritmo y cifrado de RAR 1.5).
- Rar.exe 5.50 (winrar-x64-550.exe de rarlab.com, firmado por win.rar GmbH): es de las versiones que aplican el
  filtro ARM de RAR5 (5.00 también; 6.02 en adelante ya no).
- Rar.exe 3.93 (wrar393.exe de rarlab.com, 2010, sin firma digital): todavía tiene -mci+ (filtro Itanium de
  la RarVM), que RAR 6 quitó.
- Los Rar.exe de Windows se extraen de los instaladores con 7-Zip, sin instalar.

Verificado además en la sesión que los creó con binarios reales (no versionados por su licencia): busybox
armel de Debian con Rar 5.50 y el 7z.dll IA-64 de 7z920-ia64.msi con Rar 3.93; extracción idéntica.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

msdos, rar139, rar155, rar550, rar393, unrar = sys.argv[1:7]
here = os.path.dirname(os.path.abspath(__file__))
MTIME = 1_700_000_000
PWD = "Clave123"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def lcg_bytes(n, seed):
    out = bytearray(n)
    x = seed
    for i in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out[i] = x >> 16 & 0xFF
    return bytes(out)


def x86_like(n):
    # Igual que en make_rar_fixtures.py: llamadas E8/E9 a unas pocas funciones.
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


def words_text(n):
    vocab = ("acta perito evidencia cadena custodia hash sello archivo carpeta juzgado causa expediente "
             "fecha hora disco imagen copia forense registro informe").split()
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


def arm_like(n):
    # Código ARM (modo ARM, little endian): BL (0xEB en el byte alto, desplazamiento relativo en palabras) a unas
    # pocas funciones entre instrucciones comunes. Es lo que el filtro ARM convierte a direcciones absolutas.
    ops = [0xE1A00000, 0xE59F3010, 0xE5932000, 0xE3520000, 0xE92D4010, 0xE8BD8010, 0xE2800001, 0xE1500001]
    rnd = lcg_bytes(n, 11)
    out = bytearray()
    i = 0
    while len(out) < n:
        for k in range(2):
            out += ops[rnd[i + k] % len(ops)].to_bytes(4, "little")
        target = 0x400 + (rnd[i + 2] % 40) * 0x40
        rel = (target - len(out) // 4 - 2) & 0xFFFFFF
        out += (0xEB000000 | rel).to_bytes(4, "little")
        i += 3
    return bytes(out[:n])


def ia64_like(n):
    # IA-64: paquetes de 16 bytes. Dos de cada tres con plantilla MIB (0x10/0x11), cuya ranura 2 es un br.call
    # (opcode 5 en los bits 37..40 de la ranura) con destino relativo en paquetes; el resto, plantilla MII sin
    # saltos. El filtro Itanium convierte esos destinos a absolutos.
    rnd = lcg_bytes(n, 13)
    out = bytearray()
    i = 0
    filler = 0x0008000000
    while len(out) < n:
        if rnd[i] % 3:
            template = 0x10 | (rnd[i + 1] & 1)
            target = 0x100 + (rnd[i + 2] % 40) * 4
            rel = (target - len(out) // 16) & 0xFFFFF
            slot2 = (5 << 37) | (rel << 13) | (rnd[i + 3] & 0x3F)
        else:
            template = 0x00
            slot2 = filler | (rnd[i + 3] << 6)
        slot0 = filler | (rnd[i + 4] << 6)
        slot1 = filler << 1
        bundle = template | (slot0 << 5) | (slot1 << 46) | (slot2 << 87)
        out += bundle.to_bytes(16, "little")
        i += 5
    return bytes(out[:n])


# DOS: nombres 8.3 en mayúsculas. LARGO.TXT pasa de los 64 KB de ventana de RAR 1.x.
files_dos = {
    "ACTA.TXT": ("renglon del acta, perito y evidencia\n" * 250).encode("ascii"),
    "DATOS/BINARIO.BIN": bytes((i * 2654435761 >> 13) & 0xFF for i in range(40_000)),
    "DATOS/VACIO.TXT": b"",
    "DATOS/SUB/CEROS.BIN": bytes(30_000),
    "DATOS/PROGRAMA.EXE": x86_like(100_000),
    "DATOS/LARGO.TXT": words_text(150_000),
}
dirs_dos = ["VACIA"]
files_arm = {"datos/arm.bin": arm_like(120_000), "acta.txt": files_dos["ACTA.TXT"]}
files_ia64 = {"datos/ia64.bin": ia64_like(120_000), "acta.txt": files_dos["ACTA.TXT"]}


def expected_entries(fileset, empty_dirs):
    entries = [{"name": n, "kind": "File", "sha256": sha(d)} for n, d in fileset.items()]
    dirs = set(empty_dirs)
    for n in fileset:
        parts = n.split("/")[:-1]
        dirs |= {"/".join(parts[:k]) for k in range(1, len(parts) + 1)}
    entries += [{"name": d, "kind": "Directory"} for d in sorted(dirs)]
    return sorted(entries, key=lambda e: e["name"])


def build_tree(root, fileset, empty_dirs):
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


def check_with_unrar(archive, fileset, pwd):
    # Árbitro: UnRAR tiene que extraer exactamente el mismo contenido.
    with tempfile.TemporaryDirectory() as out:
        subprocess.run([unrar, "x", "-idq", "-y", "-p" + (pwd or "-"), archive, out + os.sep],
                       check=True, stdin=subprocess.DEVNULL)
        for name, data in fileset.items():
            with open(os.path.join(out, *name.split("/")), "rb") as f:
                assert f.read() == data, (archive, name)


# nombre -> (programa, interruptores, contenido, carpetas vacías, contraseña, volúmenes)
dos = {
    "m3_v14.rar": (rar139, [], None),
    "m3_v14_pass.rar": (rar139, [], PWD),
    "m1_v15.rar": (rar155, ["-m1"], None),
    "m3_v15.rar": (rar155, ["-m3"], None),
    "m5_v15_solid.rar": (rar155, ["-m5", "-s"], None),
    "m3_v15_pass.rar": (rar155, ["-m3"], PWD),
    "m5_v15_solid_pass.rar": (rar155, ["-m5", "-s"], PWD),
}
win = {
    "m3_v5_arm.rar": (rar550, ["-ma5", "-m3"], files_arm),
    "m3_v4_ia64.rar": (rar393, ["-m3", "-mci+"], files_ia64),
}

with open(os.path.join(here, "manifest.json"), encoding="utf-8") as f:
    manifest = json.load(f)

with tempfile.TemporaryDirectory() as tmp:
    for name, (exe, switches, pwd) in dos.items():
        src = os.path.join(tmp, "s_" + name)
        build_tree(src, files_dos, dirs_dos)
        # El archivo se crea con un nombre 8.3 en la carpeta de trabajo y se renombra al copiarlo.
        flag = ["-p" + pwd] if pwd else []
        cmd = [msdos, exe, "a", "-r", "-y"] + switches + flag + ["OUT.RAR"]
        subprocess.run(cmd, cwd=src, check=True, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)
        out = os.path.join(src, "OUT.RAR")
        check_with_unrar(out, files_dos, pwd)
        shutil.copyfile(out, os.path.join(here, name))
        shown = " ".join(["rar"] + cmd[2:-1] + [name]).replace(pwd or "\0", "<contraseña>")
        version = "RAR 1.39 (DOS)" if exe == rar139 else "RAR 1.55 (DOS)"
        manifest[name] = {"command": f"{version}: {shown}", "entries": expected_entries(files_dos, dirs_dos)}
        if pwd:
            manifest[name]["password"] = pwd

    # Volúmenes viejos de RAR 1.5 (.rar, .r00, .r01…); -v20k son 20 × 1024 bytes.
    src = os.path.join(tmp, "s_vol")
    build_tree(src, files_dos, dirs_dos)
    cmd = [msdos, rar155, "a", "-r", "-y", "-m3", "-v20k", "VOL.RAR"]
    subprocess.run(cmd, cwd=src, check=True, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)
    check_with_unrar(os.path.join(src, "VOL.RAR"), files_dos, None)
    parts = []
    for part in sorted(os.listdir(src)):
        stem, ext = os.path.splitext(part)
        if stem == "VOL" and (ext == ".RAR" or ext.startswith(".R")):
            target = "vol_v15" + ext.lower()
            shutil.copyfile(os.path.join(src, part), os.path.join(here, target))
            parts.append(target)
    assert len(parts) >= 3, parts
    manifest["vol_v15.rar"] = {"command": "RAR 1.55 (DOS): rar a -r -y -m3 -v20k vol_v15.rar",
                               "volumes": parts, "entries": expected_entries(files_dos, dirs_dos)}

    for name, (exe, switches, fileset) in win.items():
        src = os.path.join(tmp, "s_" + name)
        build_tree(src, fileset, [])
        out = os.path.join(tmp, name)
        cmd = [exe, "a", "-r", "-idq", "-y"] + switches + [out, "*"]
        subprocess.run(cmd, cwd=src, check=True)
        check_with_unrar(out, fileset, None)
        shutil.copyfile(out, os.path.join(here, name))
        version = "Rar.exe 5.50" if exe == rar550 else "Rar.exe 3.93"
        manifest[name] = {"command": f"{version}: " + " ".join(["rar"] + cmd[1:-2] + [name, "*"]),
                          "entries": expected_entries(fileset, [])}

with open(os.path.join(here, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=1)
print("ok:", ", ".join(list(dos) + ["vol_v15.rar"] + list(win)))
