#!/bin/sh
set -eu
ocr_root=${1:-/opt/monergy-ocr}
mkdir -p "$ocr_root/lib" "$ocr_root/tessdata"
cp /usr/bin/tesseract "$ocr_root/tesseract"
cp /usr/share/tesseract-ocr/5/tessdata/eng.traineddata "$ocr_root/tessdata"/eng.traineddata
printf '%s  %s\n' '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2' "$ocr_root/tessdata/eng.traineddata" | sha256sum -c -
# ldd resolves the full native dependency closure on the matching Noble build image.
ocr_libraries=$(mktemp)
trap 'rm -f "$ocr_libraries"' EXIT HUP INT TERM
ldd /usr/bin/tesseract > "$ocr_libraries"
if grep -q 'not found' "$ocr_libraries"; then exit 1; fi
awk '/=> \/.* \(/ { print $3 } /^[[:space:]]*\/.* \(/ { print $1 }' "$ocr_libraries" | while IFS= read -r library; do
    cp -L "$library" "$ocr_root/lib"/
done
# Run with the copied libraries/data before handing the tree to the chiseled runtime.
LD_LIBRARY_PATH="$ocr_root/lib" "$ocr_root/tesseract" --version | head -1 | grep -Fx 'tesseract 5.3.4'
