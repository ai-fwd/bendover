using System.Collections.Generic;
using Bendover.Domain.Interfaces;
using IOAbstractions = System.IO.Abstractions;

namespace Bendover.Infrastructure;

public class FileService : IFileService
{
    private readonly IOAbstractions.IFileSystem _fileSystem;

    public FileService(IOAbstractions.IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public string ReadAllText(string path)
    {
        return _fileSystem.File.ReadAllText(path);
    }

    public IEnumerable<string> GetFiles(string path, string searchPattern)
    {
        return _fileSystem.Directory.GetFiles(path, searchPattern);
    }

    public bool DirectoryExists(string path)
    {
        return _fileSystem.Directory.Exists(path);
    }

    public void CreateDirectory(string path)
    {
        _fileSystem.Directory.CreateDirectory(path);
    }

    public bool Exists(string path)
    {
        return _fileSystem.File.Exists(path);
    }
}
