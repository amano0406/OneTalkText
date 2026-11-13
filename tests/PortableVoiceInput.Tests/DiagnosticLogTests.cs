using PortableVoiceInput.Core;

namespace PortableVoiceInput.Tests;

public sealed class DiagnosticLogTests
{
    [Fact]
    public void ErrorsIncludeDiagnosticDetailsButNotExceptionMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneTalkText-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Exception error;
            try { throw new InvalidOperationException("sk-test-secret dictated private words"); }
            catch (Exception ex) { error = ex; }
            using (var log = new DiagnosticLog(root)) log.Error("Test failure", error);
            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(root, "logs"), "*.log")));
            Assert.Contains("[ERR]", text);
            Assert.Contains("Test failure", text);
            Assert.Contains("System.InvalidOperationException", text);
            Assert.Contains(nameof(ErrorsIncludeDiagnosticDetailsButNotExceptionMessages), text);
            Assert.DoesNotContain("sk-test-secret", text);
            Assert.DoesNotContain("dictated private words", text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
