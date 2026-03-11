using System.IO;

namespace HCore.Modules.Base;

public interface IModuleFileSystem
{
    string WorkingDirectory { get; }

    string ResolvePath(string path);
    void SetWorkingDirectory(string path);
    void CreateDirectory(string path);
    Stream OpenFileStream(string path, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite);
    bool DeleteFile(string path);
    bool DeleteDirectory(string path, bool recursive = false);
    bool Exists(string path);
    bool Move(string sourcePath, string destinationPath, bool overwrite = false);
    bool Rename(string path, string newName, bool overwrite = false);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IEnumerable<string> ListDirectory(string path = ".");
    string ReadAllText(string path);
    void WriteAllText(string path, string contents, bool append = false);
    void TouchFile(string path);
}
