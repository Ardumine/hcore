using System.Text;
using HCore.Main.Fs.Cmp;

namespace HCore.Main.Fs;

public class HCoreFs
{
    private readonly string _fsPath;

    public HCoreFs(string fsPath)
    {
        _fsPath = fsPath;
    }

    public List<string> ListDirs(string path)
    {
        return Directory.GetDirectories(GetRealPath(path)).Select(s => s.Remove(0, _fsPath.Length)).ToList();
    }

    public string ReadAllLines(string path)
    {
        ThrowIfPathInvalidOrDoesNotExist(path);
        
        return File.ReadAllText(GetRealPath(path));
    }

    public HCFileStream GetFileStreamRead(string path)
    {
        ThrowIfPathInvalidOrDoesNotExist(path);
        return GetFileStream(path, FileMode.Open);
    }

    public HCFileStream GetFileStream(string path, FileMode mode)
    {
        ThrowIfPathInvalidOrDoesNotExist(path);
        return new HCFileStream(GetRealPath(path), mode);
    }


    private bool CheckIfCmp(string path)
    {
        ThrowIfPathInvalidOrDoesNotExist(path);
        return Path.GetExtension(path).Trim().ToUpper() == ".CMP";
    }

    public T? ReadEntry<T>(string path) where T : CmpFile
    {
        path += ".CMP";
        
        ThrowIfPathInvalidOrDoesNotExist(path);

        if (!CheckIfCmp(path))
            throw new NotImplementedException("Not implemented to handle other file types");

        var cmp = SolveCmp(path);
        return (T?)cmp;
    }

    public void CreateCmp(string path, CmpFile cmp)
    {
        ThrowIfPathInvalid(path);
        path += ".CMP";

        using var stream = GetFileStream(path, FileMode.OpenOrCreate);
        using var textWriter = new StreamWriter(stream);
        textWriter.Write(cmp.Serialize());
    }


    /// <summary>
    /// Solve pointers and read the Cmp file.
    /// </summary>
    /// <param name="cmpPath">The path on the HCoreFs</param>
    /// <param name="depth">The depth of search on pointers. When calling, dont use this param.</param>
    /// <returns>The Cmp file</returns>
    /// <exception cref="Exception"></exception>
    private CmpFile SolveCmp(string cmpPath, uint depth = 0)
    {
        const uint maxDepth = 10;
        if (depth > maxDepth)
            throw new Exception("Pointer depth exceed maximum depth");

        var cmp = CmpFile.Parse(ReadAllLines(cmpPath));

        if (cmp is not CmpEntryPointer pointer)
            return cmp;

        ThrowIfPathInvalidOrDoesNotExist(pointer.FilePath);
        return SolveCmp(pointer.FilePath, depth);
    }

    private string GetRealPath(string path)
    {
        ThrowIfPathInvalid(path);
        var normalized = NormalizePath(path);
        return Path.Join([_fsPath, normalized]);
    }

    private void ThrowIfPathInvalidOrDoesNotExist(string? path)
    {
        ThrowIfPathInvalid(path);
        if (!Path.Exists(GetRealPath(path!)))
            throw new Exception($"File '{path}' does not exist");
    }

    private static void ThrowIfPathInvalid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException($"Invalid path: {path}");
    }

    public static string NormalizePath(string path)
    {
        ThrowIfPathInvalid(path);
        
        var segments = path.Split('/');
        var stack = new Stack<string>();

        foreach (var segment in segments)
            if (segment == "..")
            {
                // Back and check if not pop beyond the root
                if (stack.Count >= 1)
                    stack.Pop();
            }
            else if (segment != "." && !string.IsNullOrEmpty(segment))
            {
                stack.Push(segment);
            }

        var normalizedSegments = stack.Reverse();
        return "/" + string.Join("/", normalizedSegments);
    }
}