#!/usr/bin/env bash
shopt -s nullglob
echo ""
echo "============================================"
echo "    Omega Studio - release publisher"
echo "============================================"
SOLUTION="OmegaStudio.slnx"
PROJECT="OmegaStudio/OmegaStudio.csproj"
PUBLISH_DIR="publish"
ERROR_COUNT=0
SECONDS=0
cd "$(dirname "$0")" || exit 1
publish() {
    local RUNTIME="$1"
    local SELF_CONTAINED="$2"
    local OUTPUT="$PUBLISH_DIR/$RUNTIME"
    echo ""
    echo "============================================"
    echo "   Publishing $RUNTIME  (self-contained: $SELF_CONTAINED)"
    echo "============================================"
    local EXTRA=""
    if [ "$SELF_CONTAINED" = "true" ]; then
        EXTRA="-p:EnableCompressionInSingleFile=true"
    fi
    dotnet publish "$PROJECT" -c Release -r "$RUNTIME" \
        --self-contained "$SELF_CONTAINED" \
        -p:PublishSingleFile=true \
        -p:DebugType=none \
        -p:DebugSymbols=false \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        $EXTRA \
        -o "$OUTPUT" \
        --nologo \
        --verbosity minimal
    if [ $? -ne 0 ]; then
        echo "[ERROR] $RUNTIME failed."
        ((ERROR_COUNT++))
    else
        echo "[SUCCESS] $RUNTIME -> $OUTPUT"
    fi
}
if [ -d "$PUBLISH_DIR" ]; then
    echo "[INFO] Cleaning previous publish output..."
    rm -rf "$PUBLISH_DIR"
fi
echo ""
echo "[INFO] Building all projects in Release..."
dotnet build "$SOLUTION" -c Release --nologo --verbosity minimal
if [ $? -ne 0 ]; then
    echo ""
    echo "[ERROR] Release build failed. Nothing was published."
    ((ERROR_COUNT++))
else
    publish "win-x86"     "false"
    publish "win-x64"     "false"
    publish "linux-x64"   "true"
    publish "linux-arm64" "true"
fi
echo ""
echo "============================================"
if [ "$ERROR_COUNT" -eq 0 ]; then
    echo "   PUBLISH COMPLETED SUCCESSFULLY"
    echo "   Output: $(pwd)/$PUBLISH_DIR"
    echo "============================================"
    echo ""
    for d in "$PUBLISH_DIR"/*/; do
        [ -d "$d" ] || continue
        d=${d%/} 
        echo "   $(basename "$d")"
        for f in "$d"/*; do
            if [ -f "$f" ]; then
                f_size=$(wc -c < "$f" | tr -d ' ')
                echo "     $(basename "$f")  $f_size bytes"
            fi
        done
        if [ -d "$d/formats" ]; then
            for f in "$d/formats"/*.dll; do
                if [ -f "$f" ]; then
                    f_size=$(wc -c < "$f" | tr -d ' ')
                    echo "     formats/$(basename "$f")  $f_size bytes"
                fi
            done
        else
            echo "     [WARN] no formats folder - this build opens nothing"
        fi
    done
else
    echo "   PUBLISH COMPLETED WITH ERRORS"
    echo "   Failed targets: $ERROR_COUNT"
    echo "============================================"
fi
echo ""
ELAPSED_MM=$(( SECONDS / 60 ))
ELAPSED_SS=$(( SECONDS % 60 ))
echo "[INFO] Elapsed: ${ELAPSED_MM}m ${ELAPSED_SS}s"
echo ""
read -n 1 -s -r -p "Press any key to continue..."
echo ""
exit "$ERROR_COUNT"