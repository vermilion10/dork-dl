using MessagePack;

namespace DorkDl.CriInterop;

// Msgpack wire contract for tools/cri_worker/worker.py.
// Explicit lowercase keys match Python dicts directly without casing conversion issues.
[MessagePackObject]
public sealed class DecodeAcbRequest
{
    [Key("id")]
    public int Id { get; set; }

    [Key("op")]
    public string Op { get; set; } = "decode_acb";

    // ACB path on disk (worker reads directly rather than over stdin pipe).
    [Key("acb_path")]
    public string AcbPath { get; set; } = string.Empty;

    // Sibling .awb path if waveforms stream from an external bank.
    [Key("awb_path")]
    public string? AwbPath { get; set; }

    // Directory where decoded WAVs are written.
    [Key("out_dir")]
    public string OutDir { get; set; } = string.Empty;

    // HCA decryption key.
    [Key("key")]
    public ulong Key { get; set; }
}

[MessagePackObject]
public sealed class DemuxUsmRequest
{
    [Key("id")]
    public int Id { get; set; }

    [Key("op")]
    public string Op { get; set; } = "demux_usm";

    [Key("usm_path")]
    public string UsmPath { get; set; } = string.Empty;

    // Directory where demuxed elementary streams are dumped.
    [Key("out_dir")]
    public string OutDir { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class CriResponse
{
    [Key("id")]
    public int? Id { get; set; }

    [Key("ok")]
    public bool Ok { get; set; }

    [Key("error")]
    public string? Error { get; set; }

    [Key("tracks")]
    public List<CriTrack>? Tracks { get; set; }
}

// Demuxed USM elementary streams before muxing.
[MessagePackObject]
public sealed class DemuxUsmResponse
{
    [Key("id")]
    public int? Id { get; set; }

    [Key("ok")]
    public bool Ok { get; set; }

    [Key("error")]
    public string? Error { get; set; }

    [Key("video_path")]
    public string? VideoPath { get; set; }

    [Key("audio_paths")]
    public List<string>? AudioPaths { get; set; }

    // Informational only (e.g. "60/1"). USM field rate is double the real frame rate; ffmpeg uses H.264 SPS timing.
    [Key("frame_rate")]
    public string? FrameRate { get; set; }

    [Key("width")]
    public int Width { get; set; }

    [Key("height")]
    public int Height { get; set; }

    [Key("codec")]
    public string? Codec { get; set; }
}

[MessagePackObject]
public sealed class CriTrack
{
    // Cue name from cue sheet.
    [Key("name")]
    public string Name { get; set; } = string.Empty;

    // Output WAV path.
    [Key("path")]
    public string Path { get; set; } = string.Empty;

    [Key("size")]
    public long Size { get; set; }
}
