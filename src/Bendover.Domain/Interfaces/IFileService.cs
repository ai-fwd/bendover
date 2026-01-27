using System.Collections.Generic;

namespace Bendover.Domain.Interfaces;

public interface IFileService
{
    string ReadAllText(string path);
    IEnumerable<string> GetFiles(string path, string searchPattern);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    bool Exists(string path);
}
