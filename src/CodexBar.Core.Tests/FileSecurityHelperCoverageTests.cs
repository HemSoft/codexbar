// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;

/// <summary>
/// Tests for FileSecurityHelper: WriteRestrictedFile and CreateRestrictedFileStream
/// covering platform-specific branches.
/// </summary>
public class FileSecurityHelperCoverageTests : IDisposable
{
    private readonly string _tempDir;

    public FileSecurityHelperCoverageTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_security_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void WriteRestrictedFile_WhenNewPath_CreatesFileWithContent()
    {
        var path = Path.Combine(this._tempDir, "test-write.txt");
        FileSecurityHelper.WriteRestrictedFile(path, "hello world");

        Assert.True(File.Exists(path));
        Assert.Equal("hello world", File.ReadAllText(path));
    }

    [Fact]
    public void WriteRestrictedFile_WhenFileExists_OverwritesContent()
    {
        var path = Path.Combine(this._tempDir, "test-overwrite.txt");
        File.WriteAllText(path, "original");
        FileSecurityHelper.WriteRestrictedFile(path, "updated");

        Assert.Equal("updated", File.ReadAllText(path));
    }

    [Fact]
    public void CreateRestrictedFileStream_WhenCalled_ReturnsWritableStream()
    {
        var path = Path.Combine(this._tempDir, "test-stream.txt");
        using (var stream = FileSecurityHelper.CreateRestrictedFileStream(path))
        {
            Assert.True(stream.CanWrite);
            using var writer = new StreamWriter(stream);
            writer.Write("stream content");
        }

        Assert.Equal("stream content", File.ReadAllText(path));
    }

    [Fact]
    public void CreateRestrictedFileStream_WhenNewFile_CreatesIt()
    {
        var path = Path.Combine(this._tempDir, "new-file.dat");
        Assert.False(File.Exists(path));

        using (var stream = FileSecurityHelper.CreateRestrictedFileStream(path))
        {
            using var writer = new StreamWriter(stream);
            writer.Write("created");
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteRestrictedFile_WhenEmptyContent_CreatesEmptyFile()
    {
        var path = Path.Combine(this._tempDir, "empty.txt");
        FileSecurityHelper.WriteRestrictedFile(path, string.Empty);

        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllText(path));
    }

    [Fact]
    public void WriteRestrictedFile_WhenLargeContent_WritesCorrectly()
    {
        var path = Path.Combine(this._tempDir, "large.txt");
        var content = new string('A', 100_000);
        FileSecurityHelper.WriteRestrictedFile(path, content);

        Assert.Equal(content, File.ReadAllText(path));
    }
}
