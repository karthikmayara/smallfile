using System.Collections.Generic;
namespace SmallFile.Core.Models;
/// <summary>
/// Represents a single file's metadata for sync comparison.
/// </summary>

public sealed record FileEntry(
    string RelativePath, // Normalized to forward-slashes and lower-case for Windows compatibility
    long Size, 
    long LastWriteTimeTicks, 
    string? Hash = null // Reserved for Phase 2 deduplication
);
/// <summary>
/// The SyncPlan defines a "Remote-Wins" strategy. 
/// If a file is different (Size or Timestamp), it is marked for download.
/// If a file exists locally but not on remote, it is marked for deletion.
/// </summary>
public sealed record SyncPlan(
    List<FileEntry> FilesToDownload,
    List<string> PathsToDelete
);