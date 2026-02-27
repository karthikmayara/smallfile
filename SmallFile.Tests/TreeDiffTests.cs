using System.Collections.Generic;
using SmallFile.Core.Logic;
using SmallFile.Core.Models;
using Xunit;

namespace SmallFile.Tests;

public class TreeDiffTests
{
    [Fact]
    public void Calculator_Should_Identify_New_And_Modified_Files()
    {
        // Arrange
        var local = new List<FileEntry> {
            new("file1.txt", 100, 1000),
            new("file2.txt", 200, 2000)
        };

        var remote = new List<FileEntry> {
            new("file1.txt", 100, 1000), // Same
            new("file2.txt", 205, 2000), // Size changed
            new("file3.txt", 300, 3000)  // New file
        };

        // Act
        var plan = TreeDiffCalculator.Calculate(local, remote);

        // Assert
        Assert.Equal(2, plan.FilesToDownload.Count);
        Assert.Contains(plan.FilesToDownload, f => f.RelativePath == "file2.txt");
        Assert.Contains(plan.FilesToDownload, f => f.RelativePath == "file3.txt");
    }

    [Fact]
    public void Calculator_Should_Identify_Deletions()
    {
        var local = new List<FileEntry> { new("old.txt", 10, 10) };
        var remote = new List<FileEntry>();

        var plan = TreeDiffCalculator.Calculate(local, remote);

        Assert.Single(plan.PathsToDelete);
        Assert.Equal("old.txt", plan.PathsToDelete[0]);
    }
}