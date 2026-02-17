using Bendover.Domain.Interfaces;

namespace Bendover.SDK;

public sealed class SdkFacade
{
    private readonly IBendoverSDK _sdk;

    public SdkFacade(IBendoverSDK sdk)
    {
        _sdk = sdk;
    }

    public IFileSystem File => _sdk.File;
    public IGit Git => _sdk.Git;
    public IShell Shell => _sdk.Shell;

    public void WriteFile(string path, string content) => _sdk.File.Write(path, content);
    public void DeleteFile(string path) => _sdk.File.Delete(path);
    public string ReadFile(string path) => _sdk.File.Read(path);
    public bool FileExists(string path) => _sdk.File.Exists(path);
    public string Run(string command) => _sdk.Shell.Execute(command);
}
