// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.IO;
using Xunit;

public class FileSecurityHelperTests
{
    [Fact]
    public void WriteRestrictedFile_WritesContent()
    {
        var path = Path.GetTempFileName();
        try
        {
            Configuration.FileSecurityHelper.WriteRestrictedFile(path, "secret");
            Assert.Equal("secret", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateRestrictedFileStream_ReturnsWritableStream()
    {
        var path = Path.GetTempFileName();
        File.Delete(path);
        try
        {
            using var fs = Configuration.FileSecurityHelper.CreateRestrictedFileStream(path);
            Assert.True(fs.CanWrite);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
