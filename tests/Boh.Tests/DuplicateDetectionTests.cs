using System.IO;
using Boh.Web.Services;
using Boh.Web.Tags;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

/// <summary>
/// Near-duplicate detection end to end: hashing at upload, the flag a repost carries, the
/// backfill and scan under Maintenance, and the <c>similar:</c> search.
/// </summary>
public class DuplicateDetectionTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    /// <summary>
    /// Stores one drawing of the seeded pattern. The same seed at another size or in another
    /// format is the same picture in different bytes, which is exactly a repost.
    /// </summary>
    private static async Task<PostCreateResult> PostPatternAsync(
        TestEnvironment env, uint size, MagickFormat format = MagickFormat.Png, int seed = 1)
    {
        var bytes = TestEnvironment.MakePattern(size, size, format, seed);
        return await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct);
    }

    private static async Task<int> CreatePatternAsync(
        TestEnvironment env, uint size, MagickFormat format = MagickFormat.Png, int seed = 1) =>
        (await Assert.That(await PostPatternAsync(env, size, format, seed)).IsTypeOf<PostCreateResult.Created>())!.Post.Id;

    [Test]
    public async Task An_upload_records_a_perceptual_hash()
    {
        using var env = new TestEnvironment();
        var postId = await CreatePatternAsync(env, 300);

        var post = (await env.Posts.GetAsync(postId, Ct))!;

        await Assert.That(post.PerceptualHash).IsNotNull();
        await Assert.That(post.PerceptualHashTried).IsTrue();
    }

    /// <summary>
    /// The heart of it: the repost is stored — it is a different file, and which copy is worth
    /// keeping is not something a hash can decide — but it arrives knowing what it resembles.
    /// </summary>
    [Test]
    public async Task A_repost_at_another_size_is_stored_and_flagged()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);

        var result = await PostPatternAsync(env, 160, MagickFormat.Jpeg);

        var created = (await Assert.That(result).IsTypeOf<PostCreateResult.Created>())!;
        await Assert.That(created.Post.Id).IsNotEqualTo(original);
        await Assert.That(await env.Db.Posts.CountAsync(Ct)).IsEqualTo(2);

        var flagged = await Assert.That(created.Similar).HasSingleItem();
        await Assert.That(flagged.PostId).IsEqualTo(original);
        await Assert.That(flagged.Distance <= DuplicateService.MaxDistance).IsTrue();
    }

    [Test]
    public async Task An_unrelated_upload_is_not_flagged()
    {
        using var env = new TestEnvironment();
        await CreatePatternAsync(env, 300, seed: 1);

        var result = await PostPatternAsync(env, 300, seed: 2);

        await Assert.That((await Assert.That(result).IsTypeOf<PostCreateResult.Created>())!.Similar).IsEmpty();
    }

    /// <summary>
    /// Two flat images have identical structure — none — so a hash of either would make them
    /// duplicates of each other and of every blank scan in the collection.
    /// </summary>
    [Test]
    public async Task Images_with_no_detail_are_not_flagged_against_each_other()
    {
        using var env = new TestEnvironment();

        await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(300, 300, "#4488cc")), null, "", Ct);

        var second = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(300, 300, "#cc8844")), null, "", Ct);

        var created = (await Assert.That(second).IsTypeOf<PostCreateResult.Created>())!;
        await Assert.That(created.Similar).IsEmpty();
        await Assert.That(created.Post.PerceptualHash).IsNull();

        // Attempted and recorded as such, so the backfill does not decode them again.
        await Assert.That(created.Post.PerceptualHashTried).IsTrue();
    }

    [Test]
    public async Task Byte_identical_content_is_still_an_outright_duplicate()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePattern(300, 300);

        var first = (await Assert.That(await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct)).IsTypeOf<PostCreateResult.Created>())!;

        var second = await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct);

        // Not "similar to" — the same file, which stays a hard duplicate rather than a hint.
        var duplicate = (await Assert.That(second).IsTypeOf<PostCreateResult.Duplicate>())!;
        await Assert.That(duplicate.ExistingPostId).IsEqualTo(first.Post.Id);
        await Assert.That(await env.Db.Posts.CountAsync(Ct)).IsEqualTo(1);
    }

    [Test]
    public async Task A_post_lists_what_looks_like_it_closest_first()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        var resized = await CreatePatternAsync(env, 160);
        await CreatePatternAsync(env, 300, seed: 2);

        var similar = await env.Duplicates.GetSimilarToPostAsync(original, 8, Ct);

        var found = await Assert.That(similar).HasSingleItem();
        await Assert.That(found.Post.Id).IsEqualTo(resized);
    }

    [Test]
    public async Task A_post_with_no_hash_has_no_look_alikes()
    {
        using var env = new TestEnvironment();
        await CreatePatternAsync(env, 400);

        var flat = (await Assert.That(await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(300, 300)), null, "", Ct)).IsTypeOf<PostCreateResult.Created>())!;

        await Assert.That(await env.Duplicates.GetSimilarToPostAsync(flat.Post.Id, 8, Ct)).IsEmpty();
        await Assert.That(await env.Duplicates.GetSimilarToPostAsync(9999, 8, Ct)).IsEmpty();
    }

    // ---- cached hashes ---------------------------------------------------

    [Test]
    public async Task A_post_stored_after_the_hashes_are_cached_is_found()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        await Assert.That(await env.Duplicates.FindSimilarIdsAsync(original, Ct)).IsEquivalentTo([original], CollectionOrdering.Matching);

        var resized = await CreatePatternAsync(env, 160);

        await Assert.That(await env.Duplicates.FindSimilarIdsAsync(original, Ct)).IsEquivalentTo([original, resized], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_deleted_post_leaves_the_cached_hashes()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        var resized = await CreatePatternAsync(env, 160);
        await Assert.That(await env.Duplicates.FindSimilarIdsAsync(original, Ct)).IsEquivalentTo([original, resized], CollectionOrdering.Matching);

        await Assert.That(await env.Posts.DeleteAsync(resized, Ct)).IsTrue();

        await Assert.That(await env.Duplicates.FindSimilarIdsAsync(original, Ct)).IsEquivalentTo([original], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_backfilled_hash_reaches_the_cached_hashes()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        var resized = await CreatePatternAsync(env, 160);
        await ForgetHashAsync(env, resized);
        await Assert.That(await env.Duplicates.FindSimilarIdsAsync(original, Ct)).IsEquivalentTo([original], CollectionOrdering.Matching);

        await env.Duplicates.ComputeMissingHashesAsync(null, Ct);

        await Assert.That(await env.Duplicates.FindSimilarIdsAsync(original, Ct)).IsEquivalentTo([original, resized], CollectionOrdering.Matching);
    }

    // ---- backfill ------------------------------------------------------

    /// <summary>
    /// Clears what an upload recorded, which is the state every post in an archive that
    /// predates this feature is in after the migration.
    /// </summary>
    private static async Task ForgetHashAsync(TestEnvironment env, int postId)
    {
        await env.Db.Posts
            .Where(p => p.Id == postId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.PerceptualHash, (long?)null)
                .SetProperty(p => p.PerceptualHashTried, false), Ct);

        // ExecuteUpdate goes straight to the database; the context still holds the old values.
        env.Db.ChangeTracker.Clear();
        env.HashIndex.Invalidate();
    }

    [Test]
    public async Task Backfilling_hashes_a_post_that_predates_the_feature()
    {
        using var env = new TestEnvironment();
        var postId = await CreatePatternAsync(env, 300);
        await ForgetHashAsync(env, postId);

        var result = await env.Duplicates.ComputeMissingHashesAsync(null, Ct);

        await Assert.That(result.Pending).IsEqualTo(1);
        await Assert.That(result.Hashed).IsEqualTo(1);
        await Assert.That(result.Failed).IsEqualTo(0);

        env.Db.ChangeTracker.Clear();
        await Assert.That((await env.Posts.GetAsync(postId, Ct))!.PerceptualHash).IsNotNull();
    }

    [Test]
    public async Task Backfilling_twice_finds_nothing_the_second_time()
    {
        using var env = new TestEnvironment();
        var postId = await CreatePatternAsync(env, 300);
        await ForgetHashAsync(env, postId);

        await env.Duplicates.ComputeMissingHashesAsync(null, Ct);
        var second = await env.Duplicates.ComputeMissingHashesAsync(null, Ct);

        await Assert.That(second.Pending).IsEqualTo(0);
        await Assert.That(second.Hashed).IsEqualTo(0);
    }

    /// <summary>
    /// An image with nothing to hash must be written off rather than retried, or every pass
    /// would go on re-decoding the same hopeless files.
    /// </summary>
    [Test]
    public async Task An_image_with_no_detail_is_not_offered_to_the_backfill_twice()
    {
        using var env = new TestEnvironment();
        var flat = (await Assert.That(await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(300, 300)), null, "", Ct)).IsTypeOf<PostCreateResult.Created>())!;

        await ForgetHashAsync(env, flat.Post.Id);

        var first = await env.Duplicates.ComputeMissingHashesAsync(null, Ct);
        await Assert.That(first.Pending).IsEqualTo(1);
        await Assert.That(first.Hashed).IsEqualTo(0);
        await Assert.That(first.Featureless).IsEqualTo(1);

        await Assert.That((await env.Duplicates.ComputeMissingHashesAsync(null, Ct)).Pending).IsEqualTo(0);
    }

    /// <summary>
    /// The realistic failure once originals live on their own mount: the file is temporarily
    /// unreachable, so the post has to stay pending rather than be recorded as unhashable.
    /// </summary>
    [Test]
    public async Task A_post_whose_original_is_missing_stays_pending()
    {
        using var env = new TestEnvironment();
        var postId = await CreatePatternAsync(env, 300);
        var post = (await env.Posts.GetAsync(postId, Ct))!;

        await ForgetHashAsync(env, postId);
        File.Delete(env.Store.OriginalPath(post.Sha256, post.FileExtension));

        var result = await env.Duplicates.ComputeMissingHashesAsync(null, Ct);

        await Assert.That(result.Failed).IsEqualTo(1);
        await Assert.That(result.Hashed).IsEqualTo(0);

        // Still on the list, so restoring the mount and running it again is all it takes.
        await Assert.That((await env.Duplicates.ComputeMissingHashesAsync(null, Ct)).Pending).IsEqualTo(1);
    }

    /// <summary>
    /// A failure stays pending, so the pass has to page past it. Asking again for "the next
    /// pending posts" would return the same failure every time and never reach the ones after.
    /// </summary>
    [Test]
    public async Task A_failure_does_not_stop_the_backfill_reaching_later_posts()
    {
        using var env = new TestEnvironment();
        var broken = await CreatePatternAsync(env, 300);
        var later = await CreatePatternAsync(env, 400, seed: 2);
        var brokenPost = (await env.Posts.GetAsync(broken, Ct))!;

        await ForgetHashAsync(env, broken);
        await ForgetHashAsync(env, later);
        File.Delete(env.Store.OriginalPath(brokenPost.Sha256, brokenPost.FileExtension));

        var result = await env.Duplicates.ComputeMissingHashesAsync(null, Ct);

        await Assert.That(result.Pending).IsEqualTo(2);
        await Assert.That(result.Failed).IsEqualTo(1);
        await Assert.That(result.Hashed).IsEqualTo(1);
    }

    [Test]
    public async Task Backfilling_an_empty_collection_completes_cleanly()
    {
        using var env = new TestEnvironment();

        var result = await env.Duplicates.ComputeMissingHashesAsync(null, Ct);

        await Assert.That(result.Pending).IsEqualTo(0);
    }

    // ---- archive-wide scan ---------------------------------------------

    [Test]
    public async Task The_scan_groups_one_picture_stored_three_times()
    {
        using var env = new TestEnvironment();
        var first = await CreatePatternAsync(env, 400);
        var second = await CreatePatternAsync(env, 300);
        var third = await CreatePatternAsync(env, 160, MagickFormat.Jpeg);
        await CreatePatternAsync(env, 300, seed: 2);

        var scan = await env.Duplicates.ScanForDuplicatesAsync(null, Ct);

        await Assert.That(scan.Hashed).IsEqualTo(4);

        var cluster = await Assert.That(scan.Clusters).HasSingleItem();
        await Assert.That(cluster.Size).IsEqualTo(3);
        await Assert.That(cluster.Posts.Select(p => p.Id)).IsEquivalentTo([first, second, third], CollectionOrdering.Matching);
        await Assert.That(cluster.ClosestDistance <= DuplicateService.MaxDistance).IsTrue();
        await Assert.That(scan.OmittedClusters).IsEqualTo(0);
    }

    /// <summary>
    /// A group of things that all look alike says nothing new after the first few, so the
    /// report trims what it renders while still counting what it found. Without the trim, one
    /// enormous group would put thousands of ids into a query and thousands of thumbnails on
    /// the page.
    /// </summary>
    [Test]
    public async Task A_large_group_is_counted_in_full_but_shown_in_part()
    {
        using var env = new TestEnvironment();

        // Fourteen renderings of one picture, near enough in size that each is a hair from the
        // next — which is what a folder of "img_final_v2_resized" looks like.
        for (uint size = 300; size <= 430; size += 10)
        {
            await CreatePatternAsync(env, size);
        }

        var scan = await env.Duplicates.ScanForDuplicatesAsync(null, Ct);

        var cluster = await Assert.That(scan.Clusters).HasSingleItem();
        await Assert.That(cluster.Size).IsEqualTo(14);
        await Assert.That(cluster.Posts.Count).IsEqualTo(12);

        // The oldest are the ones kept, since that is where the original is likeliest to be.
        await Assert.That(cluster.Posts.Select(p => p.Id)).IsEquivalentTo(cluster.Posts.Select(p => p.Id).Order(), CollectionOrdering.Matching);
        await Assert.That(cluster.Posts[0].Id).IsEqualTo(1);
    }

    [Test]
    public async Task The_scan_reports_nothing_when_every_picture_is_distinct()
    {
        using var env = new TestEnvironment();
        await CreatePatternAsync(env, 300, seed: 1);
        await CreatePatternAsync(env, 300, seed: 2);
        await CreatePatternAsync(env, 300, seed: 3);

        var scan = await env.Duplicates.ScanForDuplicatesAsync(null, Ct);

        await Assert.That(scan.Clusters).IsEmpty();
        await Assert.That(scan.Hashed).IsEqualTo(3);
    }

    [Test]
    public async Task The_scan_ignores_posts_with_no_hash()
    {
        using var env = new TestEnvironment();
        await env.Posts.CreateAsync(new MemoryStream(TestEnvironment.MakePng(300, 300)), null, "", Ct);
        await env.Posts.CreateAsync(new MemoryStream(TestEnvironment.MakePng(200, 200)), null, "", Ct);

        var scan = await env.Duplicates.ScanForDuplicatesAsync(null, Ct);

        await Assert.That(scan.Hashed).IsEqualTo(0);
        await Assert.That(scan.Clusters).IsEmpty();
    }

    // ---- similar: search ------------------------------------------------

    private static async Task<int[]> SearchAsync(TestEnvironment env, string query)
    {
        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse(query), Ct);
        var (posts, _) = await env.Posts.ListAsync(resolved, 1, 40, Ct);
        return [.. posts.Select(p => p.Id).OrderBy(id => id)];
    }

    [Test]
    public async Task Similar_finds_the_reference_post_and_its_look_alikes()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        var resized = await CreatePatternAsync(env, 160);
        await CreatePatternAsync(env, 300, seed: 2);

        await Assert.That(await SearchAsync(env, $"similar:{original}")).IsEquivalentTo([original, resized], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Excluding_similar_leaves_everything_that_does_not_look_like_it()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        await CreatePatternAsync(env, 160);
        var unrelated = await CreatePatternAsync(env, 300, seed: 2);

        await Assert.That(await SearchAsync(env, $"-similar:{original}")).IsEquivalentTo([unrelated], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Similar_combines_with_a_tag_term()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        var resized = await CreatePatternAsync(env, 160);

        await env.Tags.SetPostTagsAsync(resized, TagName.ParseMany("keeper"), Ct);

        await Assert.That(await SearchAsync(env, $"similar:{original} keeper")).IsEquivalentTo([resized], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Nothing can be like a post that has no hash, so the term is unanswerable — the same
    /// situation as requiring a tag no post carries, and it must match nothing rather than
    /// everything.
    /// </summary>
    [Test]
    public async Task Similar_to_a_post_with_no_hash_matches_nothing()
    {
        using var env = new TestEnvironment();
        await CreatePatternAsync(env, 400);

        var flat = (await Assert.That(await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(300, 300)), null, "", Ct)).IsTypeOf<PostCreateResult.Created>())!;

        await Assert.That(await SearchAsync(env, $"similar:{flat.Post.Id}")).IsEmpty();
        await Assert.That(await SearchAsync(env, "similar:9999")).IsEmpty();
    }

    [Test]
    public async Task Excluding_a_post_with_no_hash_excludes_nothing()
    {
        using var env = new TestEnvironment();
        var first = await CreatePatternAsync(env, 400);
        var second = await CreatePatternAsync(env, 300, seed: 2);

        await Assert.That(await SearchAsync(env, "-similar:9999")).IsEquivalentTo([first, second], CollectionOrdering.Matching);
    }

    /// <summary>Random honors the same search, so it must resolve the term too.</summary>
    [Test]
    public async Task Random_within_a_similar_search_stays_inside_it()
    {
        using var env = new TestEnvironment();
        var original = await CreatePatternAsync(env, 400);
        var resized = await CreatePatternAsync(env, 160);
        await CreatePatternAsync(env, 300, seed: 2);

        var resolved = await env.Tags.ResolveSearchAsync(
            SearchQuery.Parse($"similar:{original}"), Ct);

        for (var i = 0; i < 8; i++)
        {
            var picked = await env.Posts.GetRandomIdAsync(resolved, Ct);
            await Assert.That(new int?[] { original, resized }).Contains(picked);
        }
    }
}
