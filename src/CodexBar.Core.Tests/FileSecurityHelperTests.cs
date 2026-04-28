using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace CodexBar.Core.Tests;

public class FileSecurityHelperTests
{
    [Fact]
    public void WriteRestrictedFile_WritesContent()
    {
        var path = Path.GetTempFileName();
        try
        {
            CodexBar.Core.Configuration.FileSecurityHelper.WriteRestrictedFile(path, "secret");
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
            using var fs = CodexBar.Core.Configuration.FileSecurityHelper.CreateRestrictedFileStream(path);
            Assert.True(fs.CanWrite);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
