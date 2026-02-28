using System;
using System.IO;
using System.Threading.Tasks;
using SmallFile.Core.Logic;
using SmallFile.Core.Models;

namespace SmallFile.Core.App;

public sealed class SyncOrchestrator
{
    private readonly TransferEngine _engine;
    private readonly string _localRoot;

    public SyncOrchestrator(TransferEngine engine, string localRoot)
    {
        _engine = engine;
        _localRoot = Path.GetFullPath(localRoot);
        Directory.CreateDirectory(_localRoot);

        _engine.OnRemoteTreeRequested += HandleRemoteTreeRequested;
        _engine.OnRemoteTreeReceived += HandleRemoteTreeReceived;
        _engine.OnFileRequested += HandleFileRequested;
        _engine.OnFileChunkReceived += HandleFileChunkReceived;
        _engine.OnFileCompleteReceived += HandleFileCompleteReceived;
    }

    private void HandleRemoteTreeRequested()
    {
        var files = DirectoryScanner.Scan(_localRoot);
        _ = _engine.SendFileTreeAsync(files);
    }

    private void HandleRemoteTreeReceived(System.Collections.Generic.List<FileEntry> remoteFiles)
    {
        var localFiles = DirectoryScanner.Scan(_localRoot);
        var plan = TreeDiffCalculator.Calculate(localFiles, remoteFiles);

        foreach (var file in plan.FilesToDownload)
        {
            _ = _engine.RequestFileAsync(file.RelativePath);
        }

        foreach (var path in plan.PathsToDelete)
        {
            var fullPath = GetSafePath(path);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
    }

    private void HandleFileRequested(string relativePath)
    {
        Task.Run(async () =>
        {
            try
            {
                var fullPath = GetSafePath(relativePath);
                if (!File.Exists(fullPath)) return;

                const int chunkSize = 64 * 1024;
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, true);
                
                byte[] buffer = new byte[chunkSize];
                long offset = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    byte[] chunk = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                    await _engine.SendFileChunkAsync(relativePath, offset, chunk);
                    offset += bytesRead;
                }

                await _engine.SendFileCompleteAsync(relativePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Orchestrator] Read failed for {relativePath}: {ex.Message}");
            }
        });
    }

    private void HandleFileChunkReceived(string relativePath, long offset, byte[] data)
    {
        var tempPath = GetSafePath(relativePath) + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        // TCP and the actor loop guarantee in-order, non-concurrent execution of this event.
        // Synchronous file I/O here is safe and prevents race conditions on the file handle.
        using var stream = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.Position = offset;
        stream.Write(data, 0, data.Length);
    }

    private void HandleFileCompleteReceived(string relativePath)
    {
        var finalPath = GetSafePath(relativePath);
        var tempPath = finalPath + ".tmp";

        if (File.Exists(tempPath))
        {
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempPath, finalPath);
        }
    }

    private string GetSafePath(string relativePath)
    {
        // Prevent directory traversal attacks (e.g., "../../windows/system32/cmd.exe")
        var combined = Path.GetFullPath(Path.Combine(_localRoot, relativePath));
        if (!combined.StartsWith(_localRoot, StringComparison.OrdinalIgnoreCase)) 
        {
            throw new UnauthorizedAccessException("Directory traversal attempt blocked.");
        }
        return combined;
    }
}