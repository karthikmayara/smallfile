using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SmallFile.Core.Logic;
using SmallFile.Core.Models;

namespace SmallFile.Core.Services;

public sealed record SyncProgress(
    int TotalFiles, 
    int CompletedFiles, 
    string CurrentFileName, 
    long CurrentFileBytesTransferred, 
    long CurrentFileTotalBytes
);

public sealed class FolderSyncOrchestrator : IDisposable
{
    private readonly TransferEngine _engine;
    private readonly string _localRoot;

    // Pump State
    private readonly Queue<FileEntry> _downloadQueue = new();
    private TaskCompletionSource<bool>? _syncCompleteTcs;
    private FileEntry? _currentFile;
    private FileStream? _currentStream;
    private long _expectedOffset;

    // Progress State
    private int _totalFiles;
    private int _completedFiles;
    public event Action<SyncProgress>? OnProgress;

    public FolderSyncOrchestrator(TransferEngine engine, string localRoot)
    {
        _engine = engine;
        _localRoot = Path.GetFullPath(localRoot);
        Directory.CreateDirectory(_localRoot);

        // Server-side responder hooks
        _engine.OnRemoteTreeRequested += HandleRemoteTreeRequested;
        _engine.OnFileRequested += HandleFileRequested;

        // Client-side receive hooks
        _engine.OnFileChunkReceived += HandleFileChunkReceived;
        _engine.OnFileCompleteReceived += HandleFileCompleteReceived;
    }

    /// <summary>
    /// Executes a Server-Authoritative, One-Shot Deterministic Pull.
    /// </summary>
    public async Task SyncAsync()
    {
        if (_syncCompleteTcs != null && !_syncCompleteTcs.Task.IsCompleted)
            throw new InvalidOperationException("Sync is already in progress.");

        _syncCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _totalFiles = 0;
        _completedFiles = 0;

        // 1. Hook the one-time tree response
        var treeReceivedTcs = new TaskCompletionSource<List<FileEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<List<FileEntry>> treeHandler = files => treeReceivedTcs.TrySetResult(files);
        _engine.OnRemoteTreeReceived += treeHandler;

        try
        {
            // 2. Request Server Manifest
            await _engine.RequestRemoteTreeAsync();
            var remoteTree = await treeReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            
            // 3. Diff & Plan
            var localTree = DirectoryScanner.Scan(_localRoot);
            var plan = TreeDiffCalculator.Calculate(localTree, remoteTree);

            // 4. Purge Local
            ExecuteDeletions(plan.PathsToDelete);

            // 5. Load the Pump
            _totalFiles = plan.FilesToDownload.Count;
            foreach (var file in plan.FilesToDownload)
            {
                _downloadQueue.Enqueue(file);
            }

            // 6. Start Sequential Pump
            PumpNextFile();

            // 7. Await absolute completion
            await _syncCompleteTcs.Task;
        }
        finally
        {
            _engine.OnRemoteTreeReceived -= treeHandler;
        }
    }

    private void PumpNextFile()
    {
        if (_downloadQueue.TryDequeue(out var nextFile))
        {
            _currentFile = nextFile;
            _expectedOffset = 0;

            var finalPath = GetSafePath(nextFile.RelativePath);
            var tempPath = finalPath + ".tmp";
            
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            
            // Hold stream open exclusively for the duration of this file
            _currentStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);

            ReportProgress();
            _ = _engine.RequestFileAsync(nextFile.RelativePath);
        }
        else
        {
            _currentFile = null;
            _currentStream = null;
            _syncCompleteTcs?.TrySetResult(true);
        }
    }

    private void HandleFileChunkReceived(string relativePath, long offset, byte[] data)
    {
        // 1. Strict State Guard
        if (_currentFile == null || relativePath != _currentFile.RelativePath || _currentStream == null)
        {
            AbortCurrentFile($"Unexpected chunk for {relativePath}. Not the active file.");
            return;
        }

        // 2. Strict Offset Guard
        if (offset != _expectedOffset)
        {
            AbortCurrentFile($"Offset mismatch for {relativePath}. Expected {_expectedOffset}, got {offset}");
            return;
        }

        // 3. Write
        _currentStream.Write(data, 0, data.Length);
        _expectedOffset += data.Length;

        ReportProgress();
    }

    private void HandleFileCompleteReceived(string relativePath)
    {
        if (_currentFile == null || relativePath != _currentFile.RelativePath || _currentStream == null)
            return; // Ignore strays

        var finalPath = GetSafePath(relativePath);
        var tempPath = finalPath + ".tmp";

        // 1. Flush & Close
        _currentStream.Flush(true);
        _currentStream.Dispose();
        _currentStream = null;

        // 2. Atomic Rename
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tempPath, finalPath);

        // 3. Update State & Pump
        _completedFiles++;
        _currentFile = null;
        
        ReportProgress();
        PumpNextFile();
    }

    private void AbortCurrentFile(string reason)
    {
        Console.WriteLine($"[Orchestrator] ABORT: {reason}");
        
        if (_currentStream != null && _currentFile != null)
        {
            var tempPath = GetSafePath(_currentFile.RelativePath) + ".tmp";
            _currentStream.Dispose();
            _currentStream = null;
            
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        _currentFile = null;
        
        // Stop the pump entirely on protocol failure
        _syncCompleteTcs?.TrySetException(new InvalidDataException(reason));
    }

    private void ExecuteDeletions(List<string> pathsToDelete)
    {
        foreach (var path in pathsToDelete)
        {
            var fullPath = GetSafePath(path);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
    }

    // --- Server-Side Responder Logic ---

    private void HandleRemoteTreeRequested()
    {
        var files = DirectoryScanner.Scan(_localRoot);
        _ = _engine.SendFileTreeAsync(files);
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
                Console.WriteLine($"[Orchestrator] Send failed for {relativePath}: {ex.Message}");
            }
        });
    }

    private string GetSafePath(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_localRoot, relativePath));
        if (!combined.StartsWith(_localRoot, StringComparison.OrdinalIgnoreCase)) 
            throw new UnauthorizedAccessException("Directory traversal attempt blocked.");
        return combined;
    }

    private void ReportProgress()
    {
        if (_currentFile == null) return;
        OnProgress?.Invoke(new SyncProgress(
            _totalFiles, 
            _completedFiles, 
            _currentFile.RelativePath, 
            _expectedOffset, 
            _currentFile.Size
        ));
    }

    public void Dispose()
    {
        _engine.OnRemoteTreeRequested -= HandleRemoteTreeRequested;
        _engine.OnFileRequested -= HandleFileRequested;
        _engine.OnFileChunkReceived -= HandleFileChunkReceived;
        _engine.OnFileCompleteReceived -= HandleFileCompleteReceived;

        _currentStream?.Dispose();
    }
}