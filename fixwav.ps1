find AIRadio.Server/Data/Sounds -type f -name '*.wav' -print0 |
while IFS= read -r -d '' file; do
    tmp="${file}.tmp.wav"

    ffmpeg -y -i "$file" \
        -ar 16000 \
        -ac 1 \
        -sample_fmt s16 \
        "$tmp" >/dev/null 2>&1 &&
    mv "$tmp" "$file"
done
