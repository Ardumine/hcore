using System;
using System.Collections.Generic;
using System.Linq;

namespace HCore.Main.Vfs;

internal static class PathHelpers
{
    private static readonly char[] Separators = { '/', '\\' };

    public static string NormalizeAbsolute(string path, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.IsNullOrWhiteSpace(workingDirectory) ? "/" : workingDirectory;
        }

        var isAbsolute = path.StartsWith('/');
        var candidate = isAbsolute ? path : Combine(workingDirectory, path);
        var segments = NormalizeSegments(candidate, throwIfEscapesRoot: true);
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    public static string[] NormalizeSegments(string path, bool throwIfEscapesRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return Array.Empty<string>();
        }

        var rawSegments = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stack = new Stack<string>();
        foreach (var segment in rawSegments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
                else if (throwIfEscapesRoot)
                {
                    throw new ArgumentException($"Path '{path}' escapes above root.", nameof(path));
                }

                continue;
            }

            stack.Push(segment);
        }

        return stack.Reverse().ToArray();
    }

    public static string Combine(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return basePath;
        }

        if (relativePath.StartsWith('/'))
        {
            return relativePath;
        }

        if (basePath.EndsWith('/'))
        {
            return basePath + relativePath;
        }

        return basePath + "/" + relativePath;
    }

    public static bool IsPrefix(string[] prefix, string[] path)
    {
        if (prefix.Length > path.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(prefix[i], path[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
