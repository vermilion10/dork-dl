# /// script
# requires-python = ">=3.13"
# dependencies = [
#     "msgpack>=1.1.2",
#     "pycricodecsex[usm]>=0.0.5",
#     "pycryptodome>=3.23.0",
# ]
# ///
# CRI codec worker. Talks msgpack over stdin/stdout (self-delimiting request/response maps).
# Kept as an isolated Python process since PyCriCodecsEx has no maintained C# equivalent.
# Takes file paths and writes WAVs directly to avoid piping tens of gigabytes over stdio.
# Run via `uv run worker.py`. Logs go to stderr to avoid corrupting stdout msgpack streams.

import logging
import os
import sys

import msgpack

from acb import ACB

logging.basicConfig(stream=sys.stderr, level=logging.INFO)
logger = logging.getLogger("cri_worker")


def _safe_name(name: str) -> str:
    keep = "".join(c if c.isalnum() or c in "._- " else "_" for c in name)
    return keep.strip() or "unnamed"


def _decode_acb(req: dict) -> dict:
    acb_path = req["acb_path"]
    awb_path = req.get("awb_path") or ""
    out_dir = req["out_dir"]
    key = req["key"]

    with open(acb_path, "rb") as f:
        acb_bytes = f.read()
    awb_bytes = b""
    if awb_path and os.path.exists(awb_path):
        with open(awb_path, "rb") as f:
            awb_bytes = f.read()

    acb = ACB(acb_bytes, awb_bytes)
    tracks = acb.extract_tracks(key)

    os.makedirs(out_dir, exist_ok=True)
    written = []
    seen: dict[str, int] = {}
    for name, wav in tracks:
        base = _safe_name(str(name))
        # Cue names are not guaranteed unique within an ACB.
        count = seen.get(base, 0)
        seen[base] = count + 1
        filename = f"{base}.wav" if count == 0 else f"{base}_{count}.wav"
        path = os.path.join(out_dir, filename)
        with open(path, "wb") as f:
            f.write(wav)
        written.append({"name": str(name), "path": path, "size": len(wav)})

    return {"tracks": written}


def _demux_usm(req: dict) -> dict:
    # Demuxes USM into elementary streams (C# muxes via ffmpeg).
    # Lazy import so decode_acb still works without [usm] extras.
    from PyCriCodecsEx.usm import USM

    usm_path = req["usm_path"]
    out_dir = req["out_dir"]
    os.makedirs(out_dir, exist_ok=True)

    usm = USM(usm_path)

    video = usm.get_video()
    stream = getattr(video, "stream", {}) or {}
    video_path = os.path.join(out_dir, "video.h264")
    video.save(video_path)

    audio_paths = []
    for index, audio in enumerate(usm.get_audios()):
        path = os.path.join(out_dir, f"audio{index}.wav")
        audio.save(path)
        audio_paths.append(path)

    return {
        "video_path": video_path,
        "audio_paths": audio_paths,
        # Stream frame/field rate for info (VideoMuxer relies on SPS timing).
        "frame_rate": str(stream.get("r_frame_rate") or ""),
        "width": int(stream.get("width") or 0),
        "height": int(stream.get("height") or 0),
        "codec": str(stream.get("codec_name") or ""),
    }


_OPS = {
    "decode_acb": _decode_acb,
    "demux_usm": _demux_usm,
}


def handle(req: dict) -> dict:
    op = req.get("op")
    handler = _OPS.get(op)
    if handler is None:
        raise ValueError(f"Unknown op: {op!r}")
    return handler(req)


def main() -> None:
    # Use read1() + feed() instead of Unpacker(stdin): plain .read(n) blocks waiting
    # for full buffers and deadlocks live pipes. read1() yields immediately on short reads.
    stdin = sys.stdin.buffer
    out = sys.stdout.buffer
    packer = msgpack.Packer(use_bin_type=True)
    unpacker = msgpack.Unpacker(raw=False, max_buffer_size=0)

    logger.info("cri_worker ready")

    while True:
        chunk = stdin.read1(65536)
        if not chunk:
            break  # stdin closed

        unpacker.feed(chunk)
        for req in unpacker:
            req_id = req.get("id") if isinstance(req, dict) else None
            try:
                result = handle(req)
                response = {"id": req_id, "ok": True, "error": None, **result}
            except Exception as e:
                logger.exception("Request %s failed", req_id)
                response = {"id": req_id, "ok": False, "error": str(e), "tracks": None}

            out.write(packer.pack(response))
            out.flush()

    logger.info("stdin closed, exiting")


if __name__ == "__main__":
    main()
