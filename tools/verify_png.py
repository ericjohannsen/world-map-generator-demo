"""Independent validator for the PNGs WorldGen writes.

Decodes the file with Python's zlib rather than with the same code that produced it, so it catches
encoder bugs (bad CRCs, wrong bit depth, missing scanline filter bytes, endianness mistakes).

Usage:
    python tools/verify_png.py world-height.png [--min -6500] [--max 9000] [--preview world-preview.png]
"""

import argparse
import struct
import sys
import zlib

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def read_chunks(path):
    data = open(path, "rb").read()
    if data[:8] != PNG_SIGNATURE:
        raise ValueError(f"{path}: bad PNG signature")

    offset = 8
    chunks = []

    while offset < len(data):
        (length,) = struct.unpack(">I", data[offset:offset + 4])
        chunk_type = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        (crc,) = struct.unpack(">I", data[offset + 8 + length:offset + 12 + length])

        if zlib.crc32(chunk_type + payload) != crc:
            raise ValueError(f"{path}: bad CRC in {chunk_type!r} chunk")

        chunks.append((chunk_type, payload))
        offset += 12 + length

    if offset != len(data):
        raise ValueError(f"{path}: trailing bytes after IEND")

    return chunks


def inflate(chunks):
    return zlib.decompress(b"".join(payload for chunk_type, payload in chunks if chunk_type == b"IDAT"))


def decode_heightmap(path):
    """Returns (width, height, samples) for a 16-bit grayscale PNG with filter 0."""
    chunks = read_chunks(path)
    header = next(payload for chunk_type, payload in chunks if chunk_type == b"IHDR")
    width, height, depth, colour, compression, filter_method, interlace = struct.unpack(">IIBBBBB", header)

    if (depth, colour, interlace) != (16, 0, 0):
        raise ValueError(f"{path}: expected 16-bit, non-interlaced, grayscale data")

    raw = inflate(chunks)
    stride = width * 2

    if len(raw) != height * (stride + 1):
        raise ValueError(f"{path}: inflated size {len(raw)} != expected {height * (stride + 1)}")

    if any(raw[y * (stride + 1)] != 0 for y in range(height)):
        raise ValueError(f"{path}: unexpected scanline filter (expected 0 for every row)")

    samples = []
    for y in range(height):
        row = raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)]
        samples.extend(struct.unpack(f">{width}H", row))

    return width, height, samples


def verify_heightmap(path, low, high):
    width, height, samples = decode_heightmap(path)
    print(f"{path}: {width}x{height} depth=16 colour=0 (grayscale)")

    scale = (high - low) / 65535.0
    print(f"  sample range {min(samples)}..{max(samples)}, {len(set(samples))} distinct levels")
    print(f"  elevation range {min(samples) * scale + low:.0f}..{max(samples) * scale + low:.0f} m")


def verify_rgb(path):
    chunks = read_chunks(path)
    header = next(payload for chunk_type, payload in chunks if chunk_type == b"IHDR")
    width, height, depth, colour = struct.unpack(">IIBB", header[:10])

    print(f"{path}: {width}x{height} depth={depth} colour={colour}")

    if (depth, colour) != (8, 2):
        raise ValueError(f"{path}: expected 8-bit RGB data")

    raw = inflate(chunks)
    if len(raw) != height * (width * 3 + 1):
        raise ValueError(f"{path}: unexpected inflated size")

    if any(raw[y * (width * 3 + 1)] != 0 for y in range(height)):
        raise ValueError(f"{path}: unexpected scanline filter (expected 0 for every row)")

    print("  RGB scanlines OK")


def main():
    parser = argparse.ArgumentParser(description="Validate WorldGen PNG output independently.")
    parser.add_argument("heightmap", help="16-bit grayscale heightmap PNG")
    parser.add_argument("--min", type=float, default=-6500.0, help="elevation at sample 0 (default -6500)")
    parser.add_argument("--max", type=float, default=9000.0, help="elevation at sample 65535 (default 9000)")
    parser.add_argument("--preview", help="optional 8-bit RGB preview PNG")
    args = parser.parse_args()

    try:
        verify_heightmap(args.heightmap, args.min, args.max)
        if args.preview:
            verify_rgb(args.preview)
    except (ValueError, OSError) as error:
        print(f"FAILED: {error}", file=sys.stderr)
        return 1

    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
