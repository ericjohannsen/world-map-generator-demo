"""Independent validator for WorldGen GeoTIFF output.

Uses tifffile (not the C# writer) to decode the raster and its georeferencing, then cross-checks the values
against the 16-bit PNG and the manifest.

Requires: pip install tifffile numpy      (only for this verification tool, not for WorldGen itself)

Usage:
    python tools/verify_geotiff.py world-height.tif --png world-height.png --manifest world-manifest.json
"""

import argparse
import json
import math
import os
import sys

import numpy as np
import tifffile

from verify_png import decode_heightmap


def verify_geotiff(path, png_path, manifest_path):
    with tifffile.TiffFile(path) as tif:
        page = tif.pages[0]
        data = page.asarray()
        geo = tif.geotiff_metadata or {}

        print(f"{path}: {page.imagewidth}x{page.imagelength}, {data.dtype}, {page.compression.name}, "
              f"{page.samplesperpixel} band")

        if data.dtype != np.float32:
            raise ValueError(f"expected float32 samples, got {data.dtype}")
        if page.samplesperpixel != 1:
            raise ValueError("expected a single band")
        if not np.isfinite(data).all():
            raise ValueError("raster contains NaN or infinite elevations")

        print(f"  elevation range {data.min():.0f}..{data.max():.0f} m, mean {data.mean():.0f} m")

        scale = geo.get("ModelPixelScale")
        tiepoint = geo.get("ModelTiepoint")
        if scale is None or tiepoint is None:
            raise ValueError("missing ModelPixelScale/ModelTiepoint georeferencing")

        print(f"  pixel scale {tuple(round(v, 6) for v in scale)} degrees")
        print(f"  tiepoint {tuple(round(v, 6) for v in tiepoint)}")

        if geo.get("GTRasterTypeGeoKey") != 1:
            raise ValueError("expected GTRasterTypeGeoKey = 1 (PixelIsArea)")
        if geo.get("GeographicTypeGeoKey") != 32767:
            raise ValueError("expected a user-defined geographic CRS")
        if abs(geo.get("GeogSemiMajorAxisGeoKey", 0) - 6_371_000) > 1:
            raise ValueError("planet radius missing from the geographic CRS")

        west, north = tiepoint[3], tiepoint[4]
        lon_step, lat_step = scale[0], scale[1]
        expected_west = manifest_path and json.load(open(manifest_path))["view"]["west"]

        if expected_west is not None and abs(west - expected_west) > 1e-9:
            raise ValueError(f"western edge {west} does not match manifest ({expected_west})")

        # Pixel centres implied by the area registration must fall inside the raster's nominal window.
        centre_lon = west + lon_step / 2
        centre_lat = north - lat_step / 2
        print(f"  first pixel centre {centre_lon:.4f} E, {centre_lat:.4f} N")

    if png_path:
        width, height, samples = decode_heightmap(png_path)
        if (height, width) != data.shape:
            raise ValueError("GeoTIFF and PNG dimensions differ")

        decoded = np.array(samples, dtype=np.float64).reshape(height, width)
        decoded = decoded / 65535.0 * 15500.0 - 6500.0
        difference = np.abs(decoded - data)
        print(f"  PNG cross-check: max {difference.max():.3f} m, mean {difference.mean():.3f} m")

        if difference.max() > 0.12 + 1e-3:
            raise ValueError("PNG and GeoTIFF elevations disagree by more than half a 16-bit step")

    if manifest_path:
        manifest = json.load(open(manifest_path))
        if abs(manifest["observed"]["minElevation"] - float(data.min())) > 1.0:
            raise ValueError("manifest minimum does not match the raster")
        if abs(manifest["observed"]["maxElevation"] - float(data.max())) > 1.0:
            raise ValueError("manifest maximum does not match the raster")
        print(f"  manifest seed {manifest['seed']}, stage {manifest['stage']}, "
              f"land {manifest['observed']['landFraction'] * 100:.1f}%")


def main():
    parser = argparse.ArgumentParser(description="Validate WorldGen GeoTIFF output independently.")
    parser.add_argument("geotiff", help="float32 GeoTIFF heightmap")
    parser.add_argument("--png", help="16-bit heightmap PNG to cross-check")
    parser.add_argument("--manifest", help="JSON manifest to cross-check")
    args = parser.parse_args()

    try:
        verify_geotiff(args.geotiff, args.png, args.manifest)
    except (ValueError, OSError, KeyError) as error:
        print(f"FAILED: {error}", file=sys.stderr)
        return 1

    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
