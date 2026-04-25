"""Run OCR on an image and emit text plus coordinate data."""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path
from statistics import mean
from typing import Dict, List, Tuple

import pytesseract
from PIL import Image, ImageFilter, ImageOps

pytesseract.pytesseract.tesseract_cmd = r"C:\Program Files\Tesseract-OCR\tesseract.exe"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="OCR an image and return layout coordinates.")
    parser.add_argument("image", type=Path, help="Image file to OCR")
    parser.add_argument("--output", type=Path, required=True, help="Destination JSON file")
    parser.add_argument("--language", default="eng", help="Tesseract language code")
    parser.add_argument("--psm", type=int, default=6, help="Tesseract page segmentation mode")
    parser.add_argument("--min-confidence", type=int, default=35, help="Discard words below this confidence")
    return parser.parse_args()


def preprocess_image(image: Image.Image) -> tuple[Image.Image, tuple[float, float]]:
    prepared = ImageOps.exif_transpose(image).convert("L")
    prepared = ImageOps.autocontrast(prepared)
    prepared = prepared.filter(ImageFilter.SHARPEN)

    scale = 1.0
    longest_edge = max(prepared.size)
    if longest_edge < 1800:
        scale = 1800 / float(longest_edge)
        resized = (
            max(1, int(round(prepared.width * scale))),
            max(1, int(round(prepared.height * scale))),
        )
        prepared = prepared.resize(resized, Image.Resampling.LANCZOS)

    return prepared, (scale, scale)


def scale_box(left: int, top: int, width: int, height: int, scale_x: float, scale_y: float) -> Dict[str, int]:
    return {
        "left": int(round(left / scale_x)),
        "top": int(round(top / scale_y)),
        "width": int(round(width / scale_x)),
        "height": int(round(height / scale_y)),
    }


def extract_layout(image_path: Path, language: str, psm: int, min_confidence: int) -> Dict:
    with Image.open(image_path) as source_image:
        original = ImageOps.exif_transpose(source_image).convert("RGB")
        processed, (scale_x, scale_y) = preprocess_image(original)

    config = f"--oem 3 --psm {psm}"
    data = pytesseract.image_to_data(
        processed,
        lang=language,
        config=config,
        output_type=pytesseract.Output.DICT,
    )

    words: List[Dict] = []
    line_groups: Dict[Tuple[int, int, int, int], List[Dict]] = defaultdict(list)

    for index, raw_text in enumerate(data["text"]):
        text = raw_text.strip()
        confidence_value = data["conf"][index]

        try:
            confidence = float(confidence_value)
        except ValueError:
            continue

        if not text or confidence < min_confidence:
            continue

        box = scale_box(
            data["left"][index],
            data["top"][index],
            data["width"][index],
            data["height"][index],
            scale_x,
            scale_y,
        )

        word = {
            "text": text,
            "confidence": round(confidence, 2),
            **box,
        }
        words.append(word)

        line_key = (
            data["page_num"][index],
            data["block_num"][index],
            data["par_num"][index],
            data["line_num"][index],
        )
        word["page"] = line_key[0]
        word["block"] = line_key[1]
        word["paragraph"] = line_key[2]
        word["line"] = line_key[3]
        line_groups[line_key].append(word)

    lines: List[Dict] = []
    for line_index, (line_key, line_words) in enumerate(
        sorted(line_groups.items(), key=lambda item: (min(word["top"] for word in item[1]), min(word["left"] for word in item[1]))),
        start=1,
    ):
        line_words.sort(key=lambda word: word["left"])
        line_text = " ".join(word["text"] for word in line_words)
        left = min(word["left"] for word in line_words)
        top = min(word["top"] for word in line_words)
        right = max(word["left"] + word["width"] for word in line_words)
        bottom = max(word["top"] + word["height"] for word in line_words)
        lines.append(
            {
                "lineIndex": line_index,
                "page": line_key[0],
                "block": line_key[1],
                "paragraph": line_key[2],
                "line": line_key[3],
                "text": line_text,
                "confidence": round(mean(word["confidence"] for word in line_words), 2),
                "left": left,
                "top": top,
                "width": right - left,
                "height": bottom - top,
            }
        )

    full_text = "\n".join(line["text"] for line in lines)

    return {
        "source": image_path.name,
        "imageWidth": original.width,
        "imageHeight": original.height,
        "fullText": full_text,
        "averageConfidence": round(mean(word["confidence"] for word in words), 2) if words else 0.0,
        "lineCount": len(lines),
        "wordCount": len(words),
        "lines": lines,
        "words": [
            {
                "wordIndex": word_index,
                **word,
            }
            for word_index, word in enumerate(words, start=1)
        ],
    }


def main() -> None:
    args = parse_args()
    result = extract_layout(args.image, args.language, args.psm, args.min_confidence)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
