using System.Diagnostics;
using MessagePack;

namespace DorkDl.CriInterop;

// Talks to tools/cri_worker/worker.py over msgpack on stdin/stdout.
// Kept alive across calls to avoid repeated uv/PyCriCodecsEx startup costs. Serialized via lock.
public sealed class CriWorkerClient : IDisposable
{
    private readonly Process _process;

    // DeserializeAsync hangs on Windows live process pipes; MessagePackStreamReader frames reads properly.
    private readonly MessagePackStreamReader _responses;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;
    private bool _disposed;

    private CriWorkerClient(Process process)
    {
        _process = process;
        _responses = new MessagePackStreamReader(process.StandardOutput.BaseStream, leaveOpen: true);
    }

    // Spawns worker via `uv run` (resolves inline PEP 723 deps into a cached venv on first launch).
    public static CriWorkerClient Start(string? workerScriptPath = null)
    {
        var script = workerScriptPath ?? ResolveDefaultWorkerScript();
        if (!File.Exists(script))
        {
            throw new FileNotFoundException($"CRI worker script not found at {script}", script);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "uv",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // inherited, so worker logs go straight to our stderr
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(script);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("uv run returned no process");

        return new CriWorkerClient(process);
    }

    // Decodes an ACB, writing each cue as WAV into outputDirectory.
    public async Task<IReadOnlyList<CriTrack>> DecodeAcbAsync(
        string acbPath,
        string? awbPath,
        string outputDirectory,
        ulong key,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = new DecodeAcbRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            AcbPath = Path.GetFullPath(acbPath),
            AwbPath = awbPath is null ? null : Path.GetFullPath(awbPath),
            OutDir = Path.GetFullPath(outputDirectory),
            Key = key,
        };

        var response = await SendAsync<DecodeAcbRequest, CriResponse>(request, cancellationToken);
        if (!response.Ok)
        {
            throw new CriWorkerException(response.Error ?? "unknown worker error");
        }

        return response.Tracks ?? [];
    }

    // Demuxes a USM into elementary streams (needs VideoMuxer to combine into MP4).
    public async Task<DemuxUsmResponse> DemuxUsmAsync(
        string usmPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = new DemuxUsmRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            UsmPath = Path.GetFullPath(usmPath),
            OutDir = Path.GetFullPath(outputDirectory),
        };

        var response = await SendAsync<DemuxUsmRequest, DemuxUsmResponse>(request, cancellationToken);
        if (!response.Ok)
        {
            throw new CriWorkerException(response.Error ?? "unknown worker error");
        }

        return response;
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await MessagePackSerializer.SerializeAsync(
                _process.StandardInput.BaseStream, request, cancellationToken: cancellationToken);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);

            var frame = await _responses.ReadAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "CRI worker closed its output stream; see its log on stderr");

            return MessagePackSerializer.Deserialize<TResponse>(frame, cancellationToken: cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Probes for tools/cri_worker/worker.py next to binary or by walking up parent directories.
    private static string ResolveDefaultWorkerScript()
    {
        var relative = Path.Combine("tools", "cri_worker", "worker.py");
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        return Path.Combine(AppContext.BaseDirectory, relative);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // Closing stdin is the worker's shutdown signal.
            _process.StandardInput.Close();
            if (!_process.WaitForExit(5000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            _responses.Dispose();
            _process.Dispose();
            _gate.Dispose();
        }
    }
}

public sealed class CriWorkerException(string message) : Exception(message);
