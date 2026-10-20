"""Genera los fixtures RAR con comentarios y flujos NTFS (cmt_*.rar) y comentarios.json con lo que cada uno
debe informar: el comentario del archivo, los comentarios de cada archivo (RAR 1.5-2.x) y los flujos NTFS
alternativos con su tamaño, además del contenido de siempre.

Uso (Windows, sobre NTFS): python -I make_rar_fixtures_comentarios.py <Rar.exe 7.x> <Rar.exe 6.24> <Rar.exe 2.90>
                                     <msdos.exe> <RAR 1.55 de DOS>

Procedencia de los programas: ver make_rar_fixtures.py y make_rar_fixtures_viejos.py.

- El texto de -z va en UTF-8 con BOM para Rar.exe 7 y 6.24: sin BOM lo leen como ANSI.
- RAR5 y RAR4 guardan el comentario en una cabecera de servicio "CMT" (RAR4 lo comprime si es largo) y cada
  flujo en una cabecera "STM" después de su archivo (-os).
- RAR 2.90 guarda el comentario comprimido (algoritmo 2.0) dentro de la cabecera principal, los flujos en
  subbloques viejos (0x105) y los comentarios de archivo (comando cf, texto por la entrada estándar) dentro
  de la cabecera de cada archivo.
- RAR 1.55 de DOS comprime el comentario con el algoritmo 1.5. Su comando cf lee el teclado y no la entrada
  redirigida, así que los comentarios de archivo salen solo de RAR 2.90 (el bloque es el mismo según el
  TECHNOTE.DOC de RAR 1.55). RAR 1.39 no tiene -z: el comentario del formato 1.4 queda sin fixture.

Cada comentario se contrasta con "rar cw" de Rar.exe 7.x, que tiene que devolver el texto original.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

rar7, rar6, rar2, msdos, rar155 = sys.argv[1:6]
here = os.path.dirname(os.path.abspath(__file__))
MTIME = 1_700_000_000
PWD = "Clave123"

ACTA = b"acta de prueba\n" * 50
FOTO = bytes(range(256)) * 40
STREAMS = {
    "acta.txt:oculto": b"dato escondido en un flujo alternativo\n" * 20,
    "datos/foto.jpg:Zone.Identifier": b"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://ejemplo.invalid/foto.jpg\r\n",
}
CMT_CORTO = "Causa 1234/2026 - perito: Señal ñandú\r\n"
CMT_ASCII = "Causa 1234/2026 - perito: Senal\r\n"
CMT_LARGO = "".join(f"Linea {i:03d} del comentario largo de la causa, para forzar compresion.\r\n" for i in range(60))
CF_CORTO = "Comentario del archivo acta\n"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def build(root, dos=False, streams=True):
    names = {"acta": "ACTA.TXT", "datos": "DATOS", "foto": "FOTO.JPG"} if dos else {"acta": "acta.txt", "datos": "datos", "foto": "foto.jpg"}
    os.makedirs(os.path.join(root, names["datos"]))
    files = {names["acta"]: ACTA, names["datos"] + "/" + names["foto"]: FOTO}
    for name, data in files.items():
        with open(os.path.join(root, *name.split("/")), "wb") as f:
            f.write(data)
    if streams:
        for name, data in STREAMS.items():
            path, stream = name.rsplit(":", 1)
            with open(os.path.join(root, *path.split("/")) + ":" + stream, "wb") as f:
                f.write(data)
    for dirpath, dirnames, filenames in os.walk(root):
        for n in filenames + dirnames:
            os.utime(os.path.join(dirpath, n), (MTIME, MTIME))
    entries = [{"name": n, "kind": "File", "sha256": sha(d)} for n, d in files.items()]
    entries.append({"name": names["datos"], "kind": "Directory"})
    if streams:
        entries += [{"name": n, "kind": "AlternateStream", "size": len(d)} for n, d in STREAMS.items()]
    return sorted(entries, key=lambda e: e["name"])


def check_comment(archive, expected, pwd):
    # Árbitro: Rar.exe 7 escribe el comentario a un archivo (cw); tiene que ser el texto original.
    with tempfile.TemporaryDirectory() as out:
        target = os.path.join(out, "cmt.txt")
        subprocess.run([rar7, "cw", "-idq", "-y", "-p" + (pwd or "-"), archive, target], check=True, stdin=subprocess.DEVNULL)
        with open(target, "rb") as f:
            raw = f.read()
    # cw escribe en ANSI salvo que haga falta Unicode (entonces con BOM). Decodificar así detecta además un
    # comentario guardado mal (UTF-8 leído como ANSI da "SeÃ±al", que en ANSI vuelve a los bytes UTF-8).
    if raw.startswith(b"\xef\xbb\xbf"):
        text = raw[3:].decode("utf-8")
    elif raw.startswith(b"\xff\xfe"):
        text = raw[2:].decode("utf-16-le")
    else:
        text = raw.decode("mbcs")
    assert text == expected, (archive, raw)


manifest = {}
with tempfile.TemporaryDirectory() as tmp:
    # El archivo de comentario va con BOM: sin él, Rar.exe lo lee como ANSI y guarda "SeÃ±al".
    def cmt_file(text, name, encoding="utf-8-sig"):
        path = os.path.join(tmp, name)
        with open(path, "wb") as f:
            f.write(text.encode(encoding))
        return path

    # nombre -> (Rar.exe, interruptores, comentario, contraseña)
    modern = {
        "cmt_v5.rar": (rar7, ["-ma5"], CMT_CORTO, None),
        "cmt_v5_hpass.rar": (rar7, ["-ma5"], CMT_CORTO, PWD),
        "cmt_v4.rar": (rar6, ["-ma4"], CMT_CORTO, None),
        "cmt_v4_largo.rar": (rar6, ["-ma4"], CMT_LARGO, None),
        "cmt_v4_hpass.rar": (rar6, ["-ma4"], CMT_LARGO, PWD),
    }
    for name, (exe, switches, comment, pwd) in modern.items():
        src = os.path.join(tmp, "s_" + name)
        entries = build(src)
        out = os.path.join(tmp, name)
        flag = ["-hp" + pwd] if pwd else []
        cmd = [exe, "a", "-r", "-idq", "-y", "-os", "-z" + cmt_file(comment, name + ".txt")] + switches + flag + [out, "*"]
        subprocess.run(cmd, cwd=src, check=True)
        check_comment(out, comment, pwd)
        shutil.copyfile(out, os.path.join(here, name))
        shown = " ".join(["rar", "a", "-r", "-y", "-os", "-z<comentario>"] + switches + (["-hp<contraseña>"] if pwd else []) + [name, "*"])
        manifest[name] = {"command": shown, "comment": comment, "fileComments": {}, "entries": entries}
        if pwd:
            manifest[name]["password"] = pwd

    # RAR 2.90: comentario comprimido en la cabecera principal, flujos en subbloques viejos y comentarios de
    # archivo agregados después con cf (texto por la entrada estándar).
    old = {
        "cmt_v20.rar": (CMT_ASCII, {"acta.txt": CF_CORTO}),
        "cmt_v20_largo.rar": (CMT_LARGO, {"datos/foto.jpg": CMT_LARGO}),
    }
    for name, (comment, file_comments) in old.items():
        src = os.path.join(tmp, "s_" + name)
        entries = build(src)
        out = os.path.join(tmp, name)
        subprocess.run([rar2, "a", "-r", "-inul", "-y", "-os", "-z" + cmt_file(comment, name + ".txt", "ascii"), out, "*"],
                       cwd=src, check=True)
        for fname, text in file_comments.items():
            stdin = cmt_file(text, name + ".cf.txt", "ascii")
            with open(stdin, "rb") as f:
                subprocess.run([rar2, "cf", "-y", out, fname.replace("/", "\\")], cwd=src, check=True, stdin=f,
                               stdout=subprocess.DEVNULL)
        check_comment(out, comment, None)
        shutil.copyfile(out, os.path.join(here, name))
        manifest[name] = {"command": "Rar.exe 2.90: rar a -r -y -os -z<comentario> " + name + " *, y rar cf " +
                          ", ".join(file_comments) + " <texto>",
                          "comment": comment, "fileComments": file_comments, "entries": entries}

    # RAR 1.55 de DOS: comentario comprimido con el algoritmo 1.5.
    for name, comment in {"cmt_v15.rar": CMT_ASCII, "cmt_v15_largo.rar": CMT_LARGO}.items():
        src = os.path.join(tmp, "s_" + name)
        entries = build(src, dos=True, streams=False)
        shutil.copyfile(cmt_file(comment, name + ".txt", "ascii"), os.path.join(tmp, "CMT.TXT"))
        subprocess.run([msdos, rar155, "a", "-r", "-y", r"-z..\CMT.TXT", "OUT.RAR"], cwd=src, check=True,
                       stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)
        out = os.path.join(src, "OUT.RAR")
        check_comment(out, comment, None)
        shutil.copyfile(out, os.path.join(here, name))
        manifest[name] = {"command": "RAR 1.55 (DOS): rar a -r -y -z<comentario> " + name,
                          "comment": comment, "fileComments": {}, "entries": entries}

with open(os.path.join(here, "comentarios.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=1)
print("ok:", ", ".join(manifest))
