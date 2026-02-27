using System;
using System.Collections.Generic;
using System.IO;
using SmallFile.Core.Models;

namespace SmallFile.Core.Logic;

public static class DirectoryScanner
{
    public static List<FileEntry> Scan(string rootPath)
    {
        var files = new List<FileEntry>();
        var root = new DirectoryInfo(rootPath);

        if (!root.Exists) return files;

        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            // Normalize path: Replace \ with /, Remove leading root, Lowercase for consistency
            string relative = Path.GetRelativePath(rootPath, file.FullName)
                .Replace('\\', '/')
                .ToLowerInvariant();

            files.Add(new FileEntry(
                relative,
                file.Length,
                file.LastWriteTime.Ticks
            ));
        }

        return files;
    }
}