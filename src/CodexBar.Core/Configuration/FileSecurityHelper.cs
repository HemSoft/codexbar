using System.Runtime.InteropServices;
#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace CodexBar.Core.Configuration;

/// <summary>
/// Shared utility for writing files with owner-only permissions.
/// Ensures no window exists where sensitive files (API keys, OAuth tokens) have default permissions.
/// </summary>
internal static class FileSecurityHelper
{
    /// <summary>
    /// Creates a file with owner-only permissions and writes content to it.
    /// </summary>
    internal static void WriteRestrictedFile(string filePath, string content)
    {
        using var fs = CreateRestrictedFileStream(filePath);
        using var writer = new StreamWriter(fs);
        writer.Write(content);
    }

    /// <summary>
    /// Creates a <see cref="FileStream"/> for a new file with owner-only permissions set at creation time.
    /// On Windows: uses a <see cref="FileSecurity"/> ACL granting FullControl only to the current user.
    /// On Unix: uses <see cref="FileStreamOptions.UnixCreateMode"/> to set chmod 600 atomically.
    /// </summary>
    internal static FileStream CreateRestrictedFileStream(string filePath)
    {
#if WINDOWS
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));

                return new FileInfo(filePath).Create(
                    FileMode.Create,
                    FileSystemRights.FullControl,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.None,
                    security);
            }

            // Windows identity unavailable; create with default permissions.
            return new FileStream(filePath, FileMode.Create, FileAccess.Write);
        }
        else
#endif
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        }

        return new FileStream(filePath, FileMode.Create, FileAccess.Write);
    }
}
