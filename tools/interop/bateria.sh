#!/usr/bin/env bash
# Batería de interoperabilidad contra 7-Zip con material real.
#   Formatos:  7-Zip/bsdtar crean TAR, TAR.GZ/BZ2/XZ e ISO -> nuestra CLI extrae -> diff.
#
#   Lectura:   7z.exe comprime con cada modo -> nuestra CLI extrae -> diff contra el original.
#   Escritura: nuestra CLI comprime -> 7z.exe prueba (t) y extrae (x) -> diff contra el original.
#
# Uso:  tools/interop/bateria.sh [carpeta_de_trabajo] [carpeta_fuente]
#   carpeta_de_trabajo  por defecto $TMPDIR/pr7z-bateria (se borra y se recrea)
#   carpeta_fuente      material a comprimir; si falta, se arma uno con el código del repo, los
#                       binarios compilados (x86-64), un nombre Unicode, un archivo vacío y una
#                       carpeta vacía.
# Variables: OFFICIAL_7Z_EXE (ruta a 7z.exe), CONFIG (Release por defecto; medir siempre en Release).
# Sale con código 1 si algún modo falla.
set -u
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${1:-${TMPDIR:-/tmp}/pr7z-bateria}"
CONFIG="${CONFIG:-Release}"
Z="${OFFICIAL_7Z_EXE:-/c/Program Files/7-Zip/7z.exe}"
[ -x "$Z" ] || { echo "No encuentro 7z.exe en '$Z' (definir OFFICIAL_7Z_EXE)"; exit 2; }

dotnet build "$REPO/tools/PrZip.Cli/PrZip.Cli.csproj" -c "$CONFIG" -v q -nologo >/dev/null \
  || { echo "Falló la compilación de la CLI"; exit 2; }
CLI="$REPO/tools/PrZip.Cli/bin/$CONFIG/net10.0/PrZip.Cli.dll"

rm -rf "$WORK"; mkdir -p "$WORK/arch" "$WORK/out"
if [ $# -ge 2 ]; then
  SRC="$(cd "$2" && pwd)"
else
  SRC="$WORK/src"; mkdir -p "$SRC/code" "$SRC/bin" "$SRC/vacia"
  cp -r "$REPO/src/." "$SRC/code/"; find "$SRC/code" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
  cp "$REPO"/tools/PrZip.Cli/bin/"$CONFIG"/net10.0/*.dll "$SRC/bin/"
  printf 'año, ñandú, 漢字 y emojis 🧉\n' > "$SRC/unicode_ñ_漢.txt"
  : > "$SRC/cero.txt"
fi

FALLAS=0
fallo() { echo "$1"; FALLAS=$((FALLAS + 1)); }
ms() { awk "BEGIN{printf \"%6.1fs\", $2-$1}"; }
tam() { du -h "$1" | cut -f1; }

lectura() { local name="$1"; shift
  local pw=""; for a in "$@"; do case $a in -p*) pw="$a";; esac; done
  (cd "$SRC" && "$Z" a "$WORK/arch/l_$name.7z" ./* "$@" >/dev/null 2>&1) || { fallo "L $name: 7z a FALLÓ"; return; }
  local t0 t1; t0=$(date +%s.%N)
  timeout 600 dotnet "$CLI" x "$WORK/arch/l_$name.7z" -o"$WORK/out/l_$name" -y $pw > "$WORK/out/l_$name.log" 2>&1
  local rc=$?; t1=$(date +%s.%N)
  [ $rc -eq 0 ] || { fallo "L $name: extracción rc=$rc :: $(grep -i error "$WORK/out/l_$name.log" | head -2 | tr '\n' ' ')"; return; }
  diff -r "$SRC" "$WORK/out/l_$name" >/dev/null 2>&1 \
    || { fallo "L $name: DIFIERE ($(diff -rq "$SRC" "$WORK/out/l_$name" 2>&1 | wc -l) diferencias)"; return; }
  printf "L %-12s OK  %s  %s\n" "$name" "$(ms "$t0" "$t1")" "$(tam "$WORK/arch/l_$name.7z")"
}

escritura() { local name="$1"; shift
  local pw=""; for a in "$@"; do case $a in -p*) pw="$a";; esac; done
  local t0 t1; t0=$(date +%s.%N)
  (cd "$SRC" && timeout 900 dotnet "$CLI" a "$WORK/arch/e_$name.7z" ./* "$@" > "$WORK/out/e_$name.log" 2>&1)
  local rc=$?; t1=$(date +%s.%N)
  [ $rc -eq 0 ] || { fallo "E $name: compresión rc=$rc :: $(grep -i error "$WORK/out/e_$name.log" | head -2 | tr '\n' ' ')"; return; }
  "$Z" t "$WORK/arch/e_$name.7z" $pw >/dev/null 2>&1 || { fallo "E $name: 7z t FALLÓ"; return; }
  "$Z" x "$WORK/arch/e_$name.7z" -o"$WORK/out/e_$name" -y $pw >/dev/null 2>&1 || { fallo "E $name: 7z x FALLÓ"; return; }
  diff -r "$SRC" "$WORK/out/e_$name" >/dev/null 2>&1 \
    || { fallo "E $name: DIFIERE ($(diff -rq "$SRC" "$WORK/out/e_$name" 2>&1 | wc -l) diferencias)"; return; }
  printf "E %-12s OK  %s  %s\n" "$name" "$(ms "$t0" "$t1")" "$(tam "$WORK/arch/e_$name.7z")"
}

echo "Material: $SRC ($(du -sh "$SRC" | cut -f1)) — CLI $CONFIG — $("$Z" | sed -n 2p | cut -c1-40)"
lectura lzma2_mx5
lectura lzma2_mx9 -mx9
lectura lzma2_mx1 -mx1
lectura lzma2_mt8 -mx7 -mmt8 -md=1m
lectura lzma_mx7  -m0=lzma -mx7
lectura ppmd      -m0=ppmd
lectura bzip2     -m0=bzip2
lectura deflate   -m0=deflate
lectura deflate64 -m0=deflate64
lectura copy      -mx0
lectura nosolid   -ms=off
lectura bcj2      -m0=BCJ2 -m1=LZMA:d25 -m2=LZMA:d19 -m3=LZMA:d19 -mb0:1 -mb0s1:2 -mb0s2:3
lectura arm64     -mf=ARM64
lectura delta     -mf=Delta:4
lectura aes       -pClave123
lectura aes_mhe   -pClave123 -mhe=on

escritura lzma2_mx5
escritura lzma2_mx1 -mx1
escritura lzma2_mx9 -mx9
escritura lzma_mx5  -m0=lzma
escritura ppmd      -m0=ppmd
escritura copy      -mx0
escritura solido    -ms=on
escritura mt1       -mmt=1
escritura aes_mhe   -pClave123 -mhe=on
escritura mt_on     -mmt=on
escritura ppmd_mt   -m0=ppmd -mmt=on
escritura lzma_mt   -m0=lzma -mmt=on
escritura aes_mt    -pClave123 -mhe=on -mmt=on
escritura bzip2     -m0=bzip2
escritura bzip2_mx1 -m0=bzip2 -mx1
escritura bzip2_mt  -m0=bzip2 -mmt=on
escritura deflate   -m0=deflate
escritura deflate9  -m0=deflate -mx9 -mmt=on
escritura bzip2_aes -m0=bzip2 -pClave123 -mhe=on

# Otros formatos: los crea 7-Zip (y bsdtar si está), los extrae nuestra CLI.
BSDTAR="${BSDTAR:-/c/Windows/System32/tar.exe}"
formato() { local name="$1" archivo="$2" excluir="${3:-}"
  local t0 t1; t0=$(date +%s.%N)
  timeout 600 dotnet "$CLI" x "$archivo" -o"$WORK/out/f_$name" -y > "$WORK/out/f_$name.log" 2>&1
  local rc=$?; t1=$(date +%s.%N)
  [ $rc -eq 0 ] || { fallo "F $name: extracción rc=$rc :: $(grep -i error "$WORK/out/f_$name.log" | head -2 | tr '
' ' ')"; return; }
  local raiz="$WORK/out/f_$name"; [ -d "$raiz/src" ] && raiz="$raiz/src"
  local dx=(); [ -n "$excluir" ] && dx=(-x "$excluir")
  diff -r "${dx[@]}" "$SRC" "$raiz" >/dev/null 2>&1     || { fallo "F $name: DIFIERE ($(diff -rq "${dx[@]}" "$SRC" "$raiz" 2>&1 | wc -l) diferencias)"; return; }
  printf "F %-12s OK  %s  %s
" "$name" "$(ms "$t0" "$t1")" "$(tam "$archivo")"
}
(cd "$SRC" && "$Z" a -ttar "$WORK/arch/f.tar" ./* >/dev/null 2>&1)
"$Z" a -tgzip "$WORK/arch/f.tar.gz" "$WORK/arch/f.tar" >/dev/null 2>&1
"$Z" a -tbzip2 "$WORK/arch/f.tar.bz2" "$WORK/arch/f.tar" >/dev/null 2>&1
"$Z" a -txz "$WORK/arch/f.tar.xz" "$WORK/arch/f.tar" >/dev/null 2>&1
"$Z" a -txz -mmt=4 -mx1 "$WORK/arch/f_mt.tar.xz" "$WORK/arch/f.tar" >/dev/null 2>&1
formato tar7z    "$WORK/arch/f.tar"
formato targz7z  "$WORK/arch/f.tar.gz"
formato tarbz7z  "$WORK/arch/f.tar.bz2"
formato tarxz7z  "$WORK/arch/f.tar.xz"
formato tarxzmt  "$WORK/arch/f_mt.tar.xz"
if [ -x "$BSDTAR" ]; then
  # El tar.exe de Windows (bsdtar 3.8.8) se cae con nombres de fuera de Latin-1 (p. ej. '漢'): el archivo
  # Unicode del material se excluye al crear y al comparar. Es un defecto de esa herramienta, no del motor.
  (cd "$SRC" && MSYS_NO_PATHCONV=1 "$BSDTAR" --format iso9660 --exclude 'unicode_*' -cf ../arch/f.iso . 2>/dev/null)
  (cd "$SRC" && MSYS_NO_PATHCONV=1 "$BSDTAR" --format pax --exclude 'unicode_*' -czf ../arch/f_bsd.tgz . 2>/dev/null)
  formato iso_bsd  "$WORK/arch/f.iso" 'unicode_*'
  formato tgz_bsd  "$WORK/arch/f_bsd.tgz" 'unicode_*'
fi

if [ $FALLAS -eq 0 ]; then echo "Batería completa: todo OK"; else echo "Batería: $FALLAS modo(s) con falla"; exit 1; fi
