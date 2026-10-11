using AuditFlow.Attachment.Api.Domain;
using AuditFlow.Attachment.Api.Storage;
using AuditFlow.BuildingBlocks.Errors;
using Microsoft.Extensions.Options;

namespace AuditFlow.Attachment.Tests;

public class UploadRulesTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("  spaced name.txt  ", "spaced name.txt")]
    [InlineData(@"C:\Users\bob\secret.docx", "secret.docx")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a<b>c:d|e?.txt", "abcde.txt")]
    [InlineData("tab\tname\u0000.txt", "tabname.txt")]
    [InlineData("...", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("trailing.dots...", "trailing.dots")]
    public void File_names_are_reduced_to_a_safe_display_name(string? input, string expected)
        => Assert.Equal(expected, UploadRules.SanitizeFileName(input));

    [Fact]
    public void Long_names_are_capped_but_keep_the_extension()
    {
        var name = UploadRules.SanitizeFileName(new string('a', 400) + ".xlsx");

        Assert.Equal(UploadRules.MaxFileNameLength, name.Length);
        Assert.EndsWith(".xlsx", name);
    }

    [Theory]
    [InlineData("application/pdf", "application/pdf")]
    [InlineData(null, "application/octet-stream")]
    [InlineData("  ", "application/octet-stream")]
    [InlineData("text/plain\r\nX-Evil: 1", "application/octet-stream")]
    public void Content_type_is_recorded_only_when_well_formed(string? input, string expected)
        => Assert.Equal(expected, UploadRules.NormalizeContentType(input));

    [Fact]
    public void Content_type_that_is_too_long_is_replaced() =>
        Assert.Equal("application/octet-stream", UploadRules.NormalizeContentType(new string('x', UploadRules.MaxContentTypeLength + 1)));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10L * 1024 * 1024 + 1)]
    public void Empty_and_oversized_files_are_rejected(long length)
        => Assert.Throws<ValidationFailedException>(() => UploadRules.ValidateSize(length));

    [Theory]
    [InlineData(1)]
    [InlineData(10L * 1024 * 1024)]
    public void Files_within_the_limit_are_accepted(long length) => UploadRules.ValidateSize(length);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Tabs_one_to_five_are_valid(int tab) => UploadRules.ValidateTab(tab);

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Other_tabs_are_rejected(int tab) => Assert.Throws<ValidationFailedException>(() => UploadRules.ValidateTab(tab));
}

public class LocalFileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "auditflow-store-tests", Guid.NewGuid().ToString("N"));
    private LocalFileStore Store => new(Options.Create(new FileStoreOptions { Root = root }));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Saved_files_can_be_read_back_and_deleted()
    {
        await Store.SaveAsync("ENG-1/abc", new MemoryStream([1, 2, 3]), default);

        await using (var read = await Store.OpenReadAsync("ENG-1/abc", default))
        {
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            await read!.CopyToAsync(copy);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        }

        await Store.DeleteAsync("ENG-1/abc", default);
        Assert.Null(await Store.OpenReadAsync("ENG-1/abc", default));
    }

    [Fact]
    public async Task Missing_files_read_as_null_and_delete_is_a_no_op()
    {
        Assert.Null(await Store.OpenReadAsync("ENG-1/missing", default));
        await Store.DeleteAsync("ENG-1/missing", default);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("ENG-1/../../outside")]
    [InlineData("..")]
    public async Task Keys_that_escape_the_root_are_refused(string key)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(key, new MemoryStream([1]), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.OpenReadAsync(key, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.DeleteAsync(key, default));
    }

    [Fact]
    public async Task A_key_cannot_be_written_twice()
    {
        await Store.SaveAsync("ENG-1/dup", new MemoryStream([1]), default);

        await Assert.ThrowsAsync<IOException>(() => Store.SaveAsync("ENG-1/dup", new MemoryStream([2]), default));
    }
}
