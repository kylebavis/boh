using Boh.Web;
using Boh.Web.Storage;
using Microsoft.Extensions.Configuration;

namespace Boh.Tests;

public class StorageLayoutTests
{
    private static BohOptions Load(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return BohOptions.FromConfiguration(config);
    }

    [Fact]
    public void Everything_lives_under_the_data_path_by_default()
    {
        var options = Load();

        Assert.Equal("/data/boh.db", options.DatabasePath);
        Assert.Equal("/data/originals", options.OriginalsDir);
        Assert.Equal("/data/thumbs", options.ThumbsDir);
        Assert.Equal("/data/keys", options.KeysDir);
        Assert.Equal("/data/tmp", options.ImportTempDir);
    }

    [Fact]
    public void Moving_the_data_path_moves_everything_that_was_not_overridden()
    {
        var options = Load(("BOH_DATA_PATH", "/srv/boh"));

        Assert.Equal("/srv/boh/boh.db", options.DatabasePath);
        Assert.Equal("/srv/boh/originals", options.OriginalsDir);
        Assert.Equal("/srv/boh/thumbs", options.ThumbsDir);
    }

    [Fact]
    public void Each_location_can_be_pointed_at_separate_storage()
    {
        var options = Load(
            ("BOH_DATA_PATH", "/data"),
            ("BOH_DB_PATH", "/local/db/boh.db"),
            ("BOH_ORIGINALS_PATH", "/mnt/nas/media"),
            ("BOH_THUMBS_PATH", "/local/thumbs"),
            ("BOH_KEYS_PATH", "/local/keys"),
            ("BOH_TEMP_PATH", "/local/scratch"));

        Assert.Equal("/local/db/boh.db", options.DatabasePath);
        Assert.Equal("/mnt/nas/media", options.OriginalsDir);
        Assert.Equal("/local/thumbs", options.ThumbsDir);
        Assert.Equal("/local/keys", options.KeysDir);
        Assert.Equal("/local/scratch", options.ImportTempDir);
    }

    [Fact]
    public void Overriding_one_location_leaves_the_others_on_the_data_path()
    {
        var options = Load(("BOH_ORIGINALS_PATH", "/mnt/nas/media"));

        Assert.Equal("/mnt/nas/media", options.OriginalsDir);
        Assert.Equal("/data/boh.db", options.DatabasePath);
        Assert.Equal("/data/thumbs", options.ThumbsDir);
    }

    /// <summary>
    /// Committing an upload is a File.Move. If staging sat outside the originals volume the
    /// move would become a cross-device copy — slower, and no longer atomic.
    /// </summary>
    [Fact]
    public void Upload_staging_always_follows_the_originals_location()
    {
        var options = Load(("BOH_ORIGINALS_PATH", "/mnt/nas/media"));

        Assert.StartsWith("/mnt/nas/media", options.UploadStagingDir);
    }

    [Fact]
    public void Blank_overrides_are_ignored_rather_than_producing_empty_paths()
    {
        // An unset variable in a compose file commonly arrives as an empty string.
        var options = Load(("BOH_ORIGINALS_PATH", ""), ("BOH_DB_PATH", "   "));

        Assert.Equal("/data/originals", options.OriginalsDir);
        Assert.Equal("/data/boh.db", options.DatabasePath);
    }

    [Fact]
    public void The_connection_string_follows_the_database_override()
    {
        var options = Load(("BOH_DB_PATH", "/local/db/boh.db"));

        Assert.Contains("Data Source=/local/db/boh.db", options.ConnectionString);
    }

    // ---- filesystem probe ----------------------------------------------

    [Theory]
    [InlineData("cifs")]
    [InlineData("smb3")]
    [InlineData("nfs4")]
    [InlineData("fuse.sshfs")]
    public void Network_filesystems_are_recognized(string fsType)
    {
        Assert.True(FilesystemProbe.IsNetworkFilesystem(fsType));
    }

    [Theory]
    [InlineData("ext4")]
    [InlineData("xfs")]
    [InlineData("btrfs")]
    [InlineData("overlay")]
    [InlineData(null)]
    public void Local_filesystems_are_not_flagged(string? fsType)
    {
        Assert.False(FilesystemProbe.IsNetworkFilesystem(fsType));
    }

    [Fact]
    public void The_probe_resolves_a_real_path_on_this_host()
    {
        // Runs inside a Linux container in CI; on any other host the probe returns null
        // rather than guessing, and that is an acceptable answer.
        var fsType = FilesystemProbe.GetFilesystemType(Path.GetTempPath());

        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo"))
        {
            Assert.False(string.IsNullOrWhiteSpace(fsType));
        }
    }

    [Fact]
    public void An_unresolvable_path_does_not_throw()
    {
        Assert.Null(Record.Exception(() => FilesystemProbe.GetFilesystemType("\0invalid")));
    }
}
