"""Build a self-contained, interactive 3D globe from a WorldGen preview image.

The output is a single HTML file with the PNG embedded as a data URL, so it opens from disk, works
offline, and can be shared without a server. Rotation, zoom, auto-spin, and drag-and-drop of another
image are handled in the page.

Usage:
    python tools/make_globe.py output/world-preview.png
    python tools/make_globe.py output/tropico-preview.png -o output/tropico-globe.html
"""

import argparse
import base64
import html
import os

TEMPLATE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    margin: 0;
    background: radial-gradient(circle at 50% 45%, #0b1322 0%, #04060b 60%, #000 100%);
    color: #cfd8e3;
    font: 13px/1.45 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
    overflow: hidden;
  }
  #stage { position: relative; height: 100vh; display: grid; place-items: center; }
  canvas {
    width: min(92vmin, 880px);
    height: min(92vmin, 880px);
    cursor: grab;
    touch-action: none;
    filter: drop-shadow(0 0 42px rgba(68, 132, 220, 0.18));
  }
  canvas:active { cursor: grabbing; }
  #hud { position: absolute; top: 18px; left: 20px; pointer-events: none; }
  #hud h1 { margin: 0 0 2px; font-size: 14px; font-weight: 600; letter-spacing: 0.02em; color: #e8eef7; }
  #hud p { margin: 0; font-size: 11px; color: #7f90a6; }
  #controls {
    position: absolute; left: 20px; bottom: 18px;
    display: flex; gap: 16px; align-items: center;
    font-size: 11px; color: #93a3b8;
  }
  #controls label { display: inline-flex; align-items: center; gap: 6px; cursor: pointer; }
  #controls input[type=checkbox] { accent-color: #4d8fd6; }
  #file { display: none; }
  .button {
    border: 1px solid #263447; background: #0d1622; color: #a9bacd;
    border-radius: 5px; padding: 4px 9px; cursor: pointer; font: inherit;
  }
  .button:hover { border-color: #3a5371; color: #dbe6f3; }
  #hint {
    position: absolute; right: 20px; bottom: 18px;
    font-size: 11px; color: #5f7086; text-align: right;
  }
  @media (max-width: 720px) {
    #hint { display: none; }
  }
</style>
</head>
<body>
<div id="stage">
  <canvas id="globe" width="768" height="768"></canvas>
  <div id="hud">
    <h1>__TITLE__</h1>
    <p id="meta">__META__</p>
  </div>
  <div id="controls">
    <label><input id="spin" type="checkbox" checked> auto-rotate</label>
    <label class="button" for="file">load image</label>
    <input id="file" type="file" accept="image/*">
  </div>
  <div id="hint">drag to rotate &middot; wheel to zoom &middot; double-click to reset<br>drop a PNG anywhere to replace the texture</div>
</div>
<script>
(function () {
  "use strict";

  var EMBEDDED_SRC = "data:image/png;base64,__IMAGE_DATA__";
  var canvas = document.getElementById("globe");
  var ctx = canvas.getContext("2d", { alpha: false });
  var W = canvas.width;
  var H = canvas.height;
  var frame = ctx.createImageData(W, H);
  var pixels = frame.data;
  var texture = null;

  var yaw = -1.22;
  var pitch = 0.17;
  var distance = 3.15;
  var spin = true;
  var dragging = false;
  var lastX = 0;
  var lastY = 0;
  var dirty = true;
  var tanHalf = Math.tan(0.46);

  // Light fixed in view space: upper-left, slightly in front of the globe.
  var light = normalize(-0.42, 0.55, 0.72);

  function normalize(x, y, z) {
    var length = Math.sqrt(x * x + y * y + z * z) || 1;
    return { x: x / length, y: y / length, z: z / length };
  }

  function clamp(value, low, high) {
    return value < low ? low : value > high ? high : value;
  }

  function sampleTexture(u, v, result) {
    var w = texture.width;
    var h = texture.height;
    var data = texture.data;

    var fx = u * w - 0.5;
    var fy = v * h - 0.5;
    var x0 = Math.floor(fx);
    var y0 = Math.floor(fy);
    var tx = fx - x0;
    var ty = fy - y0;

    var x1 = x0 + 1;
    x0 = ((x0 % w) + w) % w;
    x1 = ((x1 % w) + w) % w;
    var yc0 = y0 < 0 ? 0 : y0 >= h ? h - 1 : y0;
    var yc1 = y0 + 1 < 0 ? 0 : y0 + 1 >= h ? h - 1 : y0 + 1;

    var i00 = 4 * (yc0 * w + x0);
    var i10 = 4 * (yc0 * w + x1);
    var i01 = 4 * (yc1 * w + x0);
    var i11 = 4 * (yc1 * w + x1);

    for (var channel = 0; channel < 3; channel++) {
      var top = data[i00 + channel] * (1 - tx) + data[i10 + channel] * tx;
      var bottom = data[i01 + channel] * (1 - tx) + data[i11 + channel] * tx;
      result[channel] = top * (1 - ty) + bottom * ty;
    }
  }

  var sample = [0, 0, 0];

  function render() {
    if (!texture) {
      return;
    }

    var cosYaw = Math.cos(-yaw);
    var sinYaw = Math.sin(-yaw);
    var cosPitch = Math.cos(-pitch);
    var sinPitch = Math.sin(-pitch);
    var radius = 1;
    var ox = 0;
    var oy = 0;
    var oz = distance;
    var lightX = light.x;
    var lightY = light.y;
    var lightZ = light.z;

    for (var y = 0; y < H; y++) {
      var ndcY = 1 - 2 * (y + 0.5) / H;
      for (var x = 0; x < W; x++) {
        var ndcX = 2 * (x + 0.5) / W - 1;
        var dx = ndcX * tanHalf;
        var dy = ndcY * tanHalf;
        var dz = -1;
        var inverseLength = 1 / Math.sqrt(dx * dx + dy * dy + 1);
        dx *= inverseLength;
        dy *= inverseLength;
        dz *= inverseLength;

        var b = ox * dx + oy * dy + oz * dz;
        var c = ox * ox + oy * oy + oz * oz - radius * radius;
        var discriminant = b * b - c;
        var index = 4 * (y * W + x);

        if (discriminant <= 0) {
          // Outside the globe: faint atmospheric limb.
          var impact = Math.sqrt(dx * dx + dy * dy) * distance;
          var glow = clamp((1.075 - impact) / 0.075, 0, 1);
          var edge = glow * glow * glow;
          pixels[index] = 4 + 18 * edge;
          pixels[index + 1] = 10 + 48 * edge;
          pixels[index + 2] = 20 + 112 * edge;
          pixels[index + 3] = 255;
          continue;
        }

        var t = -b - Math.sqrt(discriminant);
        var px = ox + t * dx;
        var py = oy + t * dy;
        var pz = oz + t * dz;

        // Inverse globe rotation: object = Rx(-pitch) * Ry(-yaw) * view.
        var x1 = px * cosYaw + pz * sinYaw;
        var z1 = -px * sinYaw + pz * cosYaw;
        var y2 = py * cosPitch - z1 * sinPitch;
        var z2 = py * sinPitch + z1 * cosPitch;

        var latitude = Math.asin(clamp(y2, -1, 1));
        var longitude = Math.atan2(z2, x1);
        var u = (longitude + Math.PI) / (2 * Math.PI);
        u -= Math.floor(u);
        var v = 0.5 - latitude / Math.PI;

        sampleTexture(u, v, sample);

        var diffuse = px * lightX + py * lightY + pz * lightZ;
        diffuse = diffuse < 0 ? 0 : diffuse > 1 ? 1 : diffuse;
        var shade = 0.30 + 0.78 * diffuse;

        var facing = -(px * dx + py * dy + pz * dz);
        facing = facing < 0 ? 0 : facing > 1 ? 1 : facing;
        var rim = 1 - facing;
        rim = rim * rim * rim * 0.55;

        pixels[index] = clamp(sample[0] * shade + 55 * rim, 0, 255);
        pixels[index + 1] = clamp(sample[1] * shade + 85 * rim, 0, 255);
        pixels[index + 2] = clamp(sample[2] * shade + 135 * rim, 0, 255);
        pixels[index + 3] = 255;
      }
    }

    ctx.putImageData(frame, 0, 0);
  }

  function setImage(source, name) {
    var image = new Image();
    image.onload = function () {
      var buffer = document.createElement("canvas");
      buffer.width = image.naturalWidth;
      buffer.height = image.naturalHeight;
      var bufferContext = buffer.getContext("2d", { willReadFrequently: true });
      bufferContext.drawImage(image, 0, 0);

      try {
        var imageData = bufferContext.getImageData(0, 0, buffer.width, buffer.height);
        texture = {
          width: buffer.width,
          height: buffer.height,
          data: imageData.data
        };
        document.getElementById("meta").textContent =
          (name || "texture") + " \u00b7 " + buffer.width + " \u00d7 " + buffer.height +
          " \u00b7 equirectangular";
        dirty = true;
      } catch (error) {
        document.getElementById("meta").textContent =
          "Could not read this image (canvas is cross-origin). Drop the file instead.";
      }
    };
    image.onerror = function () {
      document.getElementById("meta").textContent = "Could not load the image.";
    };
    image.src = source;
  }

  function loadFile(file) {
    if (!file || !/^image\//.test(file.type)) {
      return;
    }

    var reader = new FileReader();
    reader.onload = function () {
      setImage(reader.result, file.name);
    };
    reader.readAsDataURL(file);
  }

  canvas.addEventListener("pointerdown", function (event) {
    dragging = true;
    lastX = event.clientX;
    lastY = event.clientY;
    canvas.setPointerCapture(event.pointerId);
  });

  canvas.addEventListener("pointermove", function (event) {
    if (!dragging) {
      return;
    }

    var deltaX = event.clientX - lastX;
    var deltaY = event.clientY - lastY;
    lastX = event.clientX;
    lastY = event.clientY;
    yaw -= deltaX * 0.006;
    pitch = clamp(pitch + deltaY * 0.006, -1.45, 1.45);
    dirty = true;
  });

  function endDrag(event) {
    dragging = false;
    if (canvas.hasPointerCapture(event.pointerId)) {
      canvas.releasePointerCapture(event.pointerId);
    }
  }

  canvas.addEventListener("pointerup", endDrag);
  canvas.addEventListener("pointercancel", endDrag);

  canvas.addEventListener("wheel", function (event) {
    event.preventDefault();
    distance = clamp(distance + event.deltaY * 0.0016, 1.55, 8);
    dirty = true;
  }, { passive: false });

  canvas.addEventListener("dblclick", function () {
    yaw = -1.22;
    pitch = 0.17;
    distance = 3.15;
    dirty = true;
  });

  var stage = document.getElementById("stage");
  stage.addEventListener("dragover", function (event) {
    event.preventDefault();
  });
  stage.addEventListener("drop", function (event) {
    event.preventDefault();
    if (event.dataTransfer.files.length) {
      loadFile(event.dataTransfer.files[0]);
    }
  });

  document.getElementById("file").addEventListener("change", function (event) {
    if (event.target.files.length) {
      loadFile(event.target.files[0]);
      event.target.value = "";
    }
  });

  document.getElementById("spin").addEventListener("change", function (event) {
    spin = event.target.checked;
    dirty = true;
  });

  var previous = performance.now();
  function tick(now) {
    var elapsed = Math.min((now - previous) / 1000, 0.1);
    previous = now;

    if (spin && !dragging) {
      yaw -= elapsed * 0.14;
      dirty = true;
    }

    if (dirty) {
      render();
      dirty = false;
    }

    requestAnimationFrame(tick);
  }

  setImage(EMBEDDED_SRC, "__META_NAME__");
  requestAnimationFrame(tick);
})();
</script>
</body>
</html>
"""


def main() -> int:
    parser = argparse.ArgumentParser(description="Build a self-contained 3D globe HTML page.")
    parser.add_argument("image", help="equirectangular preview PNG")
    parser.add_argument("-o", "--out", help="output HTML (default: <image>-globe.html)")
    parser.add_argument("--title", help="page title (default: image file name)")
    args = parser.parse_args()

    with open(args.image, "rb") as image_file:
        encoded = base64.b64encode(image_file.read()).decode("ascii")

    base = os.path.splitext(args.image)[0]
    output = args.out or base + "-globe.html"
    title = args.title or os.path.splitext(os.path.basename(args.image))[0]
    meta_name = os.path.basename(args.image)
    size_kb = os.path.getsize(args.image) / 1024

    document = (
        TEMPLATE.replace("__TITLE__", html.escape(title))
        .replace("__META_NAME__", html.escape(meta_name))
        .replace("__META__", html.escape(f"{meta_name} \u00b7 {size_kb:.0f} KB"))
        .replace("__IMAGE_DATA__", encoded)
    )

    os.makedirs(os.path.dirname(os.path.abspath(output)), exist_ok=True)
    with open(output, "w", encoding="utf-8", newline="\n") as out_file:
        out_file.write(document)

    print(f"Wrote {output} ({os.path.getsize(output) / (1024 * 1024):.2f} MB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
