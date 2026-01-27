namespace Bendover.Application;

public class ScriptGenerator
{
    public string WrapCode(string rawCode)
    {
        // Wrap raw code in a .csx structure with references
        return $@"
#r ""Bendover.SDK.dll""
using Bendover.SDK;

{rawCode}
";
    }
}
