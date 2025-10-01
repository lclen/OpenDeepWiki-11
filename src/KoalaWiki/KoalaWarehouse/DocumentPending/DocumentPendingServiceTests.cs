using System.Linq;
using Xunit;

namespace KoalaWiki.KoalaWarehouse.DocumentPending;

public class DocumentPendingServiceTests
{
    [Fact]
    public void EvaluateGeneratedContent_ShouldFail_WhenContentTooShort()
    {
        var (passed, metrics, issues) = DocumentPendingService.EvaluateGeneratedContent("短内容");

        Assert.False(passed);
        Assert.NotEmpty(issues);
        Assert.Contains(issues, issue => issue.Contains("文档长度"));
    }

    [Fact]
    public void ValidateMermaidSyntax_ShouldDetectInvalidDiagram()
    {
        var markdown = """\n```mermaid\nflowchart\nA-->B\n```\n""";

        var (isValid, issues) = DocumentPendingService.ValidateMermaidSyntax(markdown);

        Assert.False(isValid);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void ValidateMermaidSyntax_ShouldPassForValidDiagram()
    {
        var markdown = """\n```mermaid\ngraph TD\n    A[Start] --> B[End]\n```\n""";

        var (isValid, issues) = DocumentPendingService.ValidateMermaidSyntax(markdown);

        Assert.True(isValid);
        Assert.Empty(issues);
    }
}
