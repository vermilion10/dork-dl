using System.Diagnostics;

namespace DorkDl.CriInterop;

// Muxes demuxed USM elementary streams into MP4 via ffmpeg on PATH.
// H.264 video is stream-copied; WAV audio tracks are encoded to AAC for MP4 compatibility.
public static class VideoMuxer
{
    // Checks whether ffmpeg is available on PATH.
    public static bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static void Mux(DemuxUsmResponse demuxed, string outputPath)
    {
        if (string.IsNullOrEmpty(demuxed.VideoPath) || !File.Exists(demuxed.VideoPath))
        {
            throw new InvalidOperationException("the worker produced no video stream to mux");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");

        // Skip -framerate: the game's H.264 has SPS timing ffmpeg respects, and probe rates are field-rate (2x).
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(demuxed.VideoPath);

        var audioPaths = demuxed.AudioPaths ?? [];
        foreach (var audio in audioPaths)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audio);
        }

        // Map all inputs explicitly so multi-language audio tracks aren't dropped by ffmpeg defaults.
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        for (var i = 0; i < audioPaths.Count; i++)
        {
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add($"{i + 1}:a:0");
        }

        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("copy");
        if (audioPaths.Count > 0)
        {
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
        }

        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start ffmpeg");

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? "no output" : stderr.Trim();
            throw new InvalidOperationException($"ffmpeg exited {process.ExitCode}: {detail}");
        }
    }
}
