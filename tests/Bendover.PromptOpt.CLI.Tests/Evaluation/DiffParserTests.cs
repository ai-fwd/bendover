using System.Linq;
using Bendover.PromptOpt.CLI.Evaluation;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class DiffParserTests
{
    [Fact]
    public void Parse_ModifiedFile_ShouldReturnCorrectStatus()
    {
        var diff = @"diff --git a/src/File.cs b/src/File.cs
index 123..456 100644
--- a/src/File.cs
+++ b/src/File.cs
@@ -1 +1 @@
-old
+new
";
        var result = DiffParser.Parse(diff).ToList();
        
        Assert.Single(result);
        Assert.Equal("src/File.cs", result[0].Path);
        Assert.Equal(FileStatus.Modified, result[0].Status);
        Assert.Contains("+new", result[0].Content);
    }

    [Fact]
    public void Parse_NewFile_ShouldReturnAdded()
    {
        var diff = @"diff --git a/new.cs b/new.cs
new file mode 100644
index 000..123
--- /dev/null
+++ b/new.cs
@@ -0,0 +1 @@
+content
";
        var result = DiffParser.Parse(diff).ToList();
        
        Assert.Single(result);
        Assert.Equal("new.cs", result[0].Path);
        Assert.Equal(FileStatus.Added, result[0].Status);
    }

    [Fact]
    public void Parse_DeletedFile_ShouldReturnDeleted()
    {
        var diff = @"diff --git a/old.cs b/old.cs
deleted file mode 100644
index 123..000
--- a/old.cs
+++ /dev/null
@@ -1 +0,0 @@
-content
";
        var result = DiffParser.Parse(diff).ToList();
        
        Assert.Single(result);
        Assert.Equal("old.cs", result[0].Path);
        Assert.Equal(FileStatus.Deleted, result[0].Status);
    }

    [Fact]
    public void Parse_MultipleFiles_ShouldReturnAll()
    {
        var diff = @"diff --git a/A.cs b/A.cs
index...
--- a/A.cs
+++ b/A.cs
@@..
+A
diff --git a/B.cs b/B.cs
new file...
--- /dev/null
+++ b/B.cs
@@..
+B
";
        var result = DiffParser.Parse(diff).ToList();
        
        Assert.Equal(2, result.Count);
        Assert.Equal("A.cs", result[0].Path);
        Assert.Equal("B.cs", result[1].Path);
    }
}
