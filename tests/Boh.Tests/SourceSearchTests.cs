using Boh.Web.Services;
using Boh.Web.Tags;

namespace Boh.Tests;

/// <summary>
/// Searching by source URL, through the same resolve-then-query path the gallery uses.
/// </summary>
public class SourceSearchTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    private const string Twitter = "https://twitter.com/someone/status/1234567890";
    private const string Flickr = "https://www.flickr.com/photos/someone/9876543210";

    /// <summary>The post ids a search finds, newest first as the gallery orders them.</summary>
    private static async Task<int[]> FindAsync(TestEnvironment env, string query)
    {
        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse(query), Ct);
        var (posts, _) = await env.Posts.ListAsync(resolved, 1, 100, Ct);

        return [.. posts.Select(p => p.Id)];
    }

    private static async Task<int> CreateWithSourceAsync(TestEnvironment env, uint size, string source)
    {
        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(size, size)), null, source, Ct);

        return (await Assert.That(result).IsTypeOf<PostCreateResult.Created>())!.Post.Id;
    }

    [Test]
    public async Task A_url_term_finds_the_posts_sourced_from_that_site()
    {
        using var env = new TestEnvironment();

        var twitter = await CreateWithSourceAsync(env, 20, Twitter);
        var flickr = await CreateWithSourceAsync(env, 21, Flickr);
        var uploaded = await env.CreatePostAsync(22);

        await Assert.That(await FindAsync(env, "url:twitter.com")).IsEquivalentTo([twitter], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "url:flickr.com")).IsEquivalentTo([flickr], CollectionOrdering.Matching);

        // Every post is still reachable with no search at all.
        await Assert.That((await FindAsync(env, "")).Length).IsEqualTo(3);
        await Assert.That(await FindAsync(env, "")).Contains(uploaded);
    }

    /// <summary>The host sits in the middle of an address, so matching has to be a substring.</summary>
    [Test]
    public async Task Any_part_of_the_address_matches()
    {
        using var env = new TestEnvironment();
        var id = await CreateWithSourceAsync(env, 23, Twitter);

        await Assert.That(await FindAsync(env, "url:https")).IsEquivalentTo([id], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "url:twitter")).IsEquivalentTo([id], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "url:someone")).IsEquivalentTo([id], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "url:1234567890")).IsEquivalentTo([id], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, $"url:{Twitter}")).IsEquivalentTo([id], CollectionOrdering.Matching);

        await Assert.That(await FindAsync(env, "url:reddit")).IsEmpty();
    }

    /// <summary>
    /// The path keeps whatever case it was stored with — only the host is lowercased on the
    /// way in — so this is a real difference in case between the column and the search text.
    /// </summary>
    [Test]
    public async Task Matching_ignores_case_on_both_sides()
    {
        using var env = new TestEnvironment();
        var id = await CreateWithSourceAsync(env, 24, "https://example.invalid/Photos/SomeOne");

        await Assert.That(await FindAsync(env, "url:photos/someone")).IsEquivalentTo([id], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "url:PHOTOS/SOMEONE")).IsEquivalentTo([id], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_negated_url_term_excludes_those_posts()
    {
        using var env = new TestEnvironment();

        var twitter = await CreateWithSourceAsync(env, 25, Twitter);
        var flickr = await CreateWithSourceAsync(env, 26, Flickr);
        var uploaded = await env.CreatePostAsync(27);

        var found = await FindAsync(env, "-url:twitter.com");

        await Assert.That(found).DoesNotContain(twitter);
        await Assert.That(found).Contains(flickr);

        // A post with no source at all does not contain the text either, so it survives.
        await Assert.That(found).Contains(uploaded);
    }

    /// <summary>
    /// A post carrying several sources matches on any of them — which is the whole point of
    /// letting it hold more than one.
    /// </summary>
    [Test]
    public async Task A_post_matches_on_any_of_its_sources()
    {
        using var env = new TestEnvironment();

        var id = await CreateWithSourceAsync(env, 28, Twitter);
        await Assert.That(await env.Posts.AddSourceAsync(id, Flickr, Ct)).IsTrue();

        await Assert.That(await FindAsync(env, "url:twitter.com")).IsEquivalentTo([id], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "url:flickr.com")).IsEquivalentTo([id], CollectionOrdering.Matching);

        // And excluding either one excludes the post, since it is reachable from both.
        await Assert.That(await FindAsync(env, "-url:flickr.com")).IsEmpty();
    }

    [Test]
    public async Task url_none_finds_the_posts_that_still_need_a_source()
    {
        using var env = new TestEnvironment();

        var sourced = await CreateWithSourceAsync(env, 29, Twitter);
        var bare = await env.CreatePostAsync(30);

        await Assert.That(await FindAsync(env, "url:none")).IsEquivalentTo([bare], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "-url:none")).IsEquivalentTo([sourced], CollectionOrdering.Matching);

        // Recording one moves the post from one side to the other.
        await Assert.That(await env.Posts.AddSourceAsync(bare, Flickr, Ct)).IsTrue();
        await Assert.That(await FindAsync(env, "url:none")).IsEmpty();
    }

    [Test]
    public async Task Url_terms_combine_with_each_other_and_with_tags()
    {
        using var env = new TestEnvironment();

        var both = await CreateWithSourceAsync(env, 31, Twitter);
        await Assert.That(await env.Posts.AddSourceAsync(both, Flickr, Ct)).IsTrue();
        await env.Tags.SetPostTagsAsync(both, TagName.ParseMany("landscape"), Ct);

        var twitterOnly = await CreateWithSourceAsync(env, 32, Twitter);
        await env.Tags.SetPostTagsAsync(twitterOnly, TagName.ParseMany("landscape"), Ct);

        var untagged = await CreateWithSourceAsync(env, 33, Twitter);
        await Assert.That(await env.Posts.AddSourceAsync(untagged, Flickr, Ct)).IsTrue();

        // Terms are ANDed, exactly as tag terms are.
        await Assert.That(await FindAsync(env, "landscape url:twitter.com url:flickr.com")).IsEquivalentTo([both], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "landscape url:twitter.com -url:flickr.com")).IsEquivalentTo([twitterOnly], CollectionOrdering.Matching);
        await Assert.That(await FindAsync(env, "landscape url:reddit.com")).IsEmpty();
    }

    /// <summary>
    /// A required tag nobody has still short-circuits the whole search, even when a source
    /// term would otherwise match plenty.
    /// </summary>
    [Test]
    public async Task An_unsatisfiable_tag_beats_a_matching_url_term()
    {
        using var env = new TestEnvironment();
        await CreateWithSourceAsync(env, 34, Twitter);

        await Assert.That(await FindAsync(env, "url:twitter.com nonexistent_tag")).IsEmpty();
    }

    /// <summary>
    /// Percent and underscore are ordinary characters in a URL. If matching were built on
    /// LIKE without escaping them, "%" would match everything and "_" any single character.
    /// </summary>
    [Test]
    public async Task Wildcard_characters_in_the_search_text_are_literal()
    {
        using var env = new TestEnvironment();

        var encoded = await CreateWithSourceAsync(env, 35, "https://example.invalid/tag/100%25_done");
        await CreateWithSourceAsync(env, 36, "https://example.invalid/tag/plain");

        await Assert.That(await FindAsync(env, "url:100%25_done")).IsEquivalentTo([encoded], CollectionOrdering.Matching);

        // A literal "%" matches only the address that actually contains one — as a wildcard it
        // would have matched the plain post too.
        await Assert.That(await FindAsync(env, "url:%")).IsEquivalentTo([encoded], CollectionOrdering.Matching);

        // And "_" matches only itself, not "l" standing in the same position.
        await Assert.That(await FindAsync(env, "url:tag/p_ain")).IsEmpty();
    }

    [Test]
    public async Task A_random_post_honors_a_url_search()
    {
        using var env = new TestEnvironment();

        var twitter = await CreateWithSourceAsync(env, 37, Twitter);
        await CreateWithSourceAsync(env, 38, Flickr);

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("url:twitter.com"), Ct);

        // Only one post can satisfy it, so the pick is deterministic.
        await Assert.That(await env.Posts.GetRandomIdAsync(resolved, Ct)).IsEqualTo(twitter);
    }
}
