using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace SmallFile.Core.Services;

public sealed class SingleFileTransferOrchestrator : IDisposable
{
    private readonly TransferEngine _engine;
    private readonly string _localRoot;

    // Tracks open file handles for incoming chunks
    private readonly ConcurrentDictionary<string, IncomingTransfer> _incomingTransfers = new();

    private sealed class IncomingTransfer : IDisposable
    {
        public FileStream Stream { get; }
        public long ExpectedOffset { get; set; }
        public string TempPath { get; }
        public string FinalPath { get; }

        public IncomingTransfer(string tempPath, string finalPath)
        {
            TempPath = tempPath;
            FinalPath = finalPath;
            // Kept open for the duration of the transfer
            Stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            ExpectedOffset = 0;
        }

        public void Dispose() => Stream.Dispose();
    }

    public SingleFileTransferOrchestrator(TransferEngine engine, string localRoot)
    {
        _engine = engine;
        _localRoot = Path.GetFullPath(localRoot);
        Directory.CreateDirectory(_localRoot);

        _engine.OnFileRequested += HandleFileRequested;
        _engine.OnFileChunkReceived += HandleFileChunkReceived;
        _engine.OnFileCompleteReceived += HandleFileCompleteReceived;
    }

    private void HandleFileRequested(string relativePath)
    {
        // Fire-and-forget to avoid blocking the Engine's actor loop
        Task.Run(async () =>
        {
            try
            {
                var fullPath = GetSafePath(relativePath);
                if (!File.Exists(fullPath)) return;

                const int chunkSize = 64 * 1024;
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true);
                
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
        var finalPath = GetSafePath(relativePath);
        var tempPath = finalPath + ".tmp";

        var transfer = _incomingTransfers.GetOrAdd(relativePath, _ => 
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            return new IncomingTransfer(tempPath, finalPath);
        });

        if (offset != transfer.ExpectedOffset)
        {
            // Fail fast on offset mismatch to prevent silent corruption
            transfer.Dispose();
            _incomingTransfers.TryRemove(relativePath, out _);
            File.Delete(tempPath);
            throw new InvalidDataException($"Offset mismatch for {relativePath}. Expected {transfer.ExpectedOffset}, got {offset}");
        }

        transfer.Stream.Write(data, 0, data.Length);
        transfer.ExpectedOffset += data.Length;
    }

    private void HandleFileCompleteReceived(string relativePath)
    {
        if (_incomingTransfers.TryRemove(relativePath, out var transfer))
        {
            transfer.Stream.Flush(true); // Guarantee data durability
            transfer.Dispose(); // Flushes and closes the stream

            if (File.Exists(transfer.FinalPath))
            {
                File.Delete(transfer.FinalPath);
            }
            
            // Atomic rename
            File.Move(transfer.TempPath, transfer.FinalPath);
        }
    }

    private string GetSafePath(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_localRoot, relativePath));
        if (!combined.StartsWith(_localRoot, StringComparison.OrdinalIgnoreCase)) 
        {
            throw new UnauthorizedAccessException("Directory traversal attempt blocked.");
        }
        return combined;
    }

    public void Dispose()
    {
        _engine.OnFileRequested -= HandleFileRequested;
        _engine.OnFileChunkReceived -= HandleFileChunkReceived;
        _engine.OnFileCompleteReceived -= HandleFileCompleteReceived;

        foreach (var transfer in _incomingTransfers.Values)
        {
            transfer.Dispose();
        }
        _incomingTransfers.Clear();
    }
}