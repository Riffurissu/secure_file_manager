using FileManager.Api.Files;
using Xunit;

namespace FileManager.Tests;

public sealed class SafePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SafePathResolver _resolver;

    public SafePathResolverTests()
    {
        _resolver = new SafePathResolver(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("docs/readme.txt", "docs/readme.txt")]
    [InlineData("docs\\readme.txt", "docs/readme.txt")]
    [InlineData("a/b/c.json", "a/b/c.json")]
    public void Accepts_safe_relative_paths(string input, string expectedRelative)
    {
        var result = _resolver.Resolve("alice", input);

        Assert.Equal(expectedRelative, result.RelativePath);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_root, "alice")) + Path.DirectorySeparatorChar, result.FullPath);
    }

    [Theory]
    [InlineData("../bob/secret.txt")]
    [InlineData("a/../../bob/secret.txt")]
    [InlineData("..\\..\\windows\\win.ini")]
    [InlineData("./file.txt")]
    [InlineData("a/./b")]
    public void Rejects_traversal(string input)
    {
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("alice", input));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:file.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    public void Rejects_absolute_and_drive_paths(string input)
    {
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("alice", input));
    }

    [Theory]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("file<name.txt")]
    [InlineData("file>name.txt")]
    [InlineData("file|name.txt")]
    [InlineData("file?.txt")]
    [InlineData("file*.txt")]
    [InlineData("file\"name.txt")]
    [InlineData("file\0name.txt")]
    [InlineData("tab\tname.txt")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData(" name.txt")]
    public void Rejects_invalid_names(string input)
    {
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("alice", input));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("nul.tar.gz")]
    [InlineData("dir/LPT1.log")]
    [InlineData("COM9")]
    public void Rejects_reserved_windows_names(string input)
    {
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("alice", input));
    }

    [Fact]
    public void Rejects_too_long_path_and_segment()
    {
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("alice", new string('a', 1025)));
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("alice", new string('a', 256)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_path_is_rejected_unless_root_allowed(string? input)
    {
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("alice", input));

        var root = _resolver.Resolve("alice", input, allowRoot: true);
        Assert.Equal(string.Empty, root.RelativePath);
    }

    [Fact]
    public void Rejects_invalid_username()
    {
        Assert.Throws<PathValidationException>(() => _resolver.Resolve("../alice", "file.txt"));
    }

    [Fact]
    public void Result_stays_inside_home_for_nested_existing_directories()
    {
        var home = Path.Combine(_root, "alice");
        Directory.CreateDirectory(Path.Combine(home, "sub"));

        var result = _resolver.Resolve("alice", "sub/file.txt");

        Assert.Equal("sub/file.txt", result.RelativePath);
    }
}
