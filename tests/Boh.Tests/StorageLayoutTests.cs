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

    [Test]
    public async Task Everything_lives_under_the_data_path_by_default()
    {
        var options = Load();

        await Assert.That(options.DatabasePath).IsEqualTo("/data/boh.db");
        await Assert.That(options.OriginalsDir).IsEqualTo("/data/originals");
        await Assert.That(options.ThumbsDir).IsEqualTo("/data/thumbs");
        await Assert.That(options.KeysDir).IsEqualTo("/data/keys");
        await Assert.That(options.ImportTempDir).IsEqualTo("/data/tmp");
    }

    [Test]
    public async Task Moving_the_data_path_moves_everything_that_was_not_overridden()
    {
        var options = Load(("BOH_DATA_PATH", "/srv/boh"));

        await Assert.That(options.DatabasePath).IsEqualTo("/srv/boh/boh.db");
        await Assert.That(options.OriginalsDir).IsEqualTo("/srv/boh/originals");
        await Assert.That(options.ThumbsDir).IsEqualTo("/srv/boh/thumbs");
    }

    [Test]
    public async Task Each_location_can_be_pointed_at_separate_storage()
    {
        var options = Load(
            ("BOH_DATA_PATH", "/data"),
            ("BOH_DB_PATH", "/local/db/boh.db"),
            ("BOH_ORIGINALS_PATH", "/mnt/nas/media"),
            ("BOH_THUMBS_PATH", "/local/thumbs"),
            ("BOH_KEYS_PATH", "/local/keys"),
            ("BOH_TEMP_PATH", "/local/scratch"));

        await Assert.That(options.DatabasePath).IsEqualTo("/local/db/boh.db");
        await Assert.That(options.OriginalsDir).IsEqualTo("/mnt/nas/media");
        await Assert.That(options.ThumbsDir).IsEqualTo("/local/thumbs");
        await Assert.That(options.KeysDir).IsEqualTo("/local/keys");
        await Assert.That(options.ImportTempDir).IsEqualTo("/local/scratch");
    }

    [Test]
    public async Task Overriding_one_location_leaves_the_others_on_the_data_path()
    {
        var options = Load(("BOH_ORIGINALS_PATH", "/mnt/nas/media"));

        await Assert.That(options.OriginalsDir).IsEqualTo("/mnt/nas/media");
        await Assert.That(options.DatabasePath).IsEqualTo("/data/boh.db");
        await Assert.That(options.ThumbsDir).IsEqualTo("/data/thumbs");
    }

    /// <summary>
    /// Committing an upload is a File.Move. If staging sat outside the originals volume the
    /// move would become a cross-device copy — slower, and no longer atomic.
    /// </summary>
    [Test]
    public async Task Upload_staging_always_follows_the_originals_location()
    {
        var options = Load(("BOH_ORIGINALS_PATH", "/mnt/nas/media"));

        await Assert.That(options.UploadStagingDir).StartsWith("/mnt/nas/media");
    }

    [Test]
    public async Task Blank_overrides_are_ignored_rather_than_producing_empty_paths()
    {
        // An unset variable in a compose file commonly arrives as an empty string.
        var options = Load(("BOH_ORIGINALS_PATH", ""), ("BOH_DB_PATH", "   "));

        await Assert.That(options.OriginalsDir).IsEqualTo("/data/originals");
        await Assert.That(options.DatabasePath).IsEqualTo("/data/boh.db");
    }

    [Test]
    public async Task The_connection_string_follows_the_database_override()
    {
        var options = Load(("BOH_DB_PATH", "/local/db/boh.db"));

        await Assert.That(options.ConnectionString).Contains("Data Source=/local/db/boh.db");
    }

    // ---- filesystem probe ----------------------------------------------

    [Test]
    [Arguments("cifs")]
    [Arguments("smb2")]
    [Arguments("nfs4")]
    [Arguments("fuse")]
    public async Task Network_filesystems_are_recognized(string fsType)
    {
        await Assert.That(FilesystemProbe.IsNetworkFilesystem(fsType)).IsTrue();
    }

    [Test]
    [Arguments("ext4")]
    [Arguments("xfs")]
    [Arguments("btrfs")]
    [Arguments("overlay")]
    [Arguments(null)]
    public async Task Local_filesystems_are_not_flagged(string? fsType)
    {
        await Assert.That(FilesystemProbe.IsNetworkFilesystem(fsType)).IsFalse();
    }

    [Test]
    public async Task The_probe_resolves_a_real_path_on_this_host()
    {
        // Runs inside a Linux container in CI; on any other host the probe returns null
        // rather than guessing, and that is an acceptable answer.
        var fsType = FilesystemProbe.GetFilesystemType(Path.GetTempPath());

        if (OperatingSystem.IsLinux())
        {
            await Assert.That(string.IsNullOrWhiteSpace(fsType)).IsFalse();
        }
    }

    [Test]
    public async Task An_unresolvable_path_does_not_throw()
    {
        await Assert.That(() => FilesystemProbe.GetFilesystemType("\0invalid")).ThrowsNothing();
    }
}
