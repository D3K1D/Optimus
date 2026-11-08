using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OptimizationTool.Providers.Tests;

public sealed class DpapiCredentialStoreTests : IDisposable
{
    readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ot-providers-tests", Guid.NewGuid().ToString("N"));

    public DpapiCredentialStoreTests() => Directory.CreateDirectory(_dataDir);

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Remembered_key_round_trips_through_a_new_instance_and_is_encrypted_on_disk()
    {
        new DpapiCredentialStore(_dataDir).Save("anthropic_api", TestKeys.Anthropic, remember: true);

        var path = Path.Combine(_dataDir, "secrets", "anthropic_api.bin");
        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("TESTKEY", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("TESTKEY", Encoding.Unicode.GetString(bytes));
        Assert.False(File.Exists(path + ".tmp"));

        var fresh = new DpapiCredentialStore(_dataDir);
        Assert.True(fresh.Has("anthropic_api"));
        var loaded = fresh.Load("anthropic_api");
        Assert.NotNull(loaded);
        Assert.Equal(TestKeys.Anthropic, loaded.ApiKey);
        Assert.Equal("anthropic_api", loaded.Kind);
        Assert.DoesNotContain("TESTKEY", loaded.ToString());
        Assert.DoesNotContain("TESTKEY", fresh.ToString());
        Assert.False(fresh.Has("openai_api"));
        Assert.Null(fresh.Load("openai_api"));
    }

    [Fact]
    public void Blob_is_bound_to_the_kind()
    {
        new DpapiCredentialStore(_dataDir).Save("anthropic_api", TestKeys.Anthropic, remember: true);
        var secrets = Path.Combine(_dataDir, "secrets");
        File.Copy(Path.Combine(secrets, "anthropic_api.bin"), Path.Combine(secrets, "openai_api.bin"));
        Assert.Null(new DpapiCredentialStore(_dataDir).Load("openai_api")); // different entropy: unprotect fails
    }

    [Fact]
    public void Session_only_key_never_touches_disk()
    {
        var store = new DpapiCredentialStore(_dataDir);
        store.Save("openai_api", TestKeys.OpenAi, remember: false);
        Assert.True(store.Has("openai_api"));
        Assert.Equal(TestKeys.OpenAi, store.Load("openai_api")!.ApiKey);
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "secrets")) && Directory.EnumerateFileSystemEntries(Path.Combine(_dataDir, "secrets")).Any());
        Assert.False(new DpapiCredentialStore(_dataDir).Has("openai_api"));
    }

    [Fact]
    public void Switching_to_session_only_removes_the_remembered_file()
    {
        var store = new DpapiCredentialStore(_dataDir);
        store.Save("openai_api", TestKeys.OpenAi, remember: true);
        store.Save("openai_api", TestKeys.OpenAi + "2", remember: false);
        Assert.False(File.Exists(Path.Combine(_dataDir, "secrets", "openai_api.bin")));
        Assert.Equal(TestKeys.OpenAi + "2", store.Load("openai_api")!.ApiKey);
    }

    [Fact]
    public void File_and_folder_acl_is_current_user_and_system_only_without_inheritance()
    {
        new DpapiCredentialStore(_dataDir).Save("anthropic_api", TestKeys.Anthropic, remember: true);
        using var me = WindowsIdentity.GetCurrent();
        var expected = new HashSet<string> { me.User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };

        var fileAcl = new FileInfo(Path.Combine(_dataDir, "secrets", "anthropic_api.bin")).GetAccessControl();
        Assert.True(fileAcl.AreAccessRulesProtected);
        var fileRules = fileAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(fileRules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Equal(expected, fileRules.Select(r => r.IdentityReference.Value).ToHashSet());

        var dirAcl = new DirectoryInfo(Path.Combine(_dataDir, "secrets")).GetAccessControl();
        Assert.True(dirAcl.AreAccessRulesProtected);
        Assert.Equal(expected, dirAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet());
    }

    [Fact]
    public void Delete_removes_memory_and_disk()
    {
        var store = new DpapiCredentialStore(_dataDir);
        store.Save("anthropic_api", TestKeys.Anthropic, remember: true);
        store.Delete("anthropic_api");
        Assert.False(store.Has("anthropic_api"));
        Assert.Null(store.Load("anthropic_api"));
        Assert.False(File.Exists(Path.Combine(_dataDir, "secrets", "anthropic_api.bin")));
        Assert.False(new DpapiCredentialStore(_dataDir).Has("anthropic_api"));
        store.Delete("anthropic_api"); // idempotent
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("Anthropic_API")]
    public void Kind_must_be_a_plain_identifier_and_errors_never_echo_the_key(string kind)
    {
        var store = new DpapiCredentialStore(_dataDir);
        var ex = Assert.Throws<ArgumentException>(() => store.Save(kind, TestKeys.Anthropic, remember: true));
        Assert.DoesNotContain("TESTKEY", ex.ToString());
    }

    [Fact]
    public void A_failed_remembered_save_throws_leaves_no_temp_file_and_keeps_nothing()
    {
        var store = new DpapiCredentialStore(_dataDir);
        var path = store.PathFor("anthropic_api");
        Directory.CreateDirectory(path); // the target cannot be written (stands in for a locked or redirected profile)

        var ex = Assert.ThrowsAny<Exception>(() => store.Save("anthropic_api", TestKeys.Anthropic, remember: true));
        Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().Name);
        Assert.DoesNotContain("TESTKEY", ex.ToString());
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Null(store.Load("anthropic_api"));

        // Use without remembering still works.
        store.Save("anthropic_api", TestKeys.Anthropic, remember: false);
        Assert.Equal(TestKeys.Anthropic, store.Load("anthropic_api")!.ApiKey);
    }
}
