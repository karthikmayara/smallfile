using System.Collections.Generic;
using System.Linq;
using SmallFile.Core.Models;

namespace SmallFile.Core.Logic;

public static class TreeDiffCalculator
{
    /// <summary>
    /// Calculates the difference between the local state and a remote manifest.
    /// Policy: Remote is the Source of Truth.
    /// </summary>
    public static SyncPlan Calculate(
        List<FileEntry> localFiles,
        List<FileEntry> remoteFiles)
    {
        var localMap = localFiles.ToDictionary(f => f.RelativePath);
        var remoteMap = remoteFiles.ToDictionary(f => f.RelativePath);

        // Files to Download:
        // Exists on remote AND
        //   (doesn't exist locally OR size differs OR timestamp differs)
        var toDownload = remoteFiles
            .Where(rf =>
                !localMap.TryGetValue(rf.RelativePath, out var lf) ||
                rf.Size != lf.Size ||
                rf.LastWriteTimeTicks != lf.LastWriteTimeTicks)
            .ToList();

        // Files to Delete:
        // Exists locally but missing from remote
        var toDelete = localFiles
            .Where(lf => !remoteMap.ContainsKey(lf.RelativePath))
            .Select(lf => lf.RelativePath)
            .ToList();

        return new SyncPlan(toDownload, toDelete);
    }
}