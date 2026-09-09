using Boh.Web.Services;
using Boh.Web.Tags;

namespace Boh.Tests;

/// <summary>
/// Searching by source URL, through the same resolve-then-query path the gallery uses.
/// </summary>
public class SourceSearchTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private const string Pixiv = "https://www.pixiv.net/en/artworks/98765432";
    private const string Danbooru = "https://danbooru.donmai.us/posts/1234567";

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

        return Assert.IsType<PostCreateResult.Created>(result).Post.Id;
    }

    [Fact]
    public async Task A_url_term_finds_the_posts_sourced_from_that_site()
    {
        using var env = new TestEnvironment();

        var pixiv = await CreateWithSourceAsync(env, 20, Pixiv);
        var danbooru = await CreateWithSourceAsync(env, 21, Danbooru);
        var uploaded = await env.CreatePostAsync(22);

        Assert.Equal([pixiv], await FindAsync(env, "url:pixiv.net"));
        Assert.Equal([danbooru], await FindAsync(env, "url:donmai.us"));

        // Every post is still reachable with no search at all.
        Assert.Equal(3, (await FindAsync(env, "")).Length);
        Assert.Contains(uploaded, await FindAsync(env, ""));
    }

    /// <summary>The host sits in the middle of an address, so matching has to be a substring.</summary>
    [Fact]
    public async Task Any_part_of_the_address_matches()
    {
        using var env = new TestEnvironment();
        var id = await CreateWithSourceAsync(env, 23, Pixiv);

        Assert.Equal([id], await FindAsync(env, "url:https"));
        Assert.Equal([id], await FindAsync(env, "url:pixiv"));
        Assert.Equal([id], await FindAsync(env, "url:artworks"));
        Assert.Equal([id], await FindAsync(env, "url:98765432"));
        Assert.Equal([id], await FindAsync(env, $"url:{Pixiv}"));

        Assert.Empty(await FindAsync(env, "url:tumblr"));
    }

    [Fact]
    public async Task Matching_ignores_case_on_both_sides()
    {
        using var env = new TestEnvironment();
        var id = await CreateWithSourceAsync(env, 24, "https://WWW.Pixiv.NET/en/artworks/1");

        Assert.Equal([id], await FindAsync(env, "url:pixiv.net"));
        Assert.Equal([id], await FindAsync(env, "url:PIXIV.NET"));
    }

    [Fact]
    public async Task A_negated_url_term_excludes_those_posts()
    {
        using var env = new TestEnvironment();

        var pixiv = await CreateWithSourceAsync(env, 25, Pixiv);
        var danbooru = await CreateWithSourceAsync(env, 26, Danbooru);
        var uploaded = await env.CreatePostAsync(27);

        var found = await FindAsync(env, "-url:pixiv.net");

        Assert.DoesNotContain(pixiv, found);
        Assert.Contains(danbooru, found);

        // A post with no source at all does not contain the text either, so it survives.
        Assert.Contains(uploaded, found);
    }

    /// <summary>
    /// A post carrying several sources matches on any of them — which is the whole point of
    /// letting it hold more than one.
    /// </summary>
    [Fact]
    public async Task A_post_matches_on_any_of_its_sources()
    {
        using var env = new TestEnvironment();

        var id = await CreateWithSourceAsync(env, 28, Pixiv);
        Assert.True(await env.Posts.AddSourceAsync(id, Danbooru, Ct));

        Assert.Equal([id], await FindAsync(env, "url:pixiv.net"));
        Assert.Equal([id], await FindAsync(env, "url:donmai.us"));

        // And excluding either one excludes the post, since it is reachable from both.
        Assert.Empty(await FindAsync(env, "-url:donmai.us"));
    }

    [Fact]
    public async Task url_none_finds_the_posts_that_still_need_a_source()
    {
        using var env = new TestEnvironment();

        var sourced = await CreateWithSourceAsync(env, 29, Pixiv);
        var bare = await env.CreatePostAsync(30);

        Assert.Equal([bare], await FindAsync(env, "url:none"));
        Assert.Equal([sourced], await FindAsync(env, "-url:none"));

        // Recording one moves the post from one side to the other.
        Assert.True(await env.Posts.AddSourceAsync(bare, Danbooru, Ct));
        Assert.Empty(await FindAsync(env, "url:none"));
    }

    [Fact]
    public async Task Url_terms_combine_with_each_other_and_with_tags()
    {
        using var env = new TestEnvironment();

        var both = await CreateWithSourceAsync(env, 31, Pixiv);
        Assert.True(await env.Posts.AddSourceAsync(both, Danbooru, Ct));
        await env.Tags.SetPostTagsAsync(both, TagName.ParseMany("landscape"), Ct);

        var pixivOnly = await CreateWithSourceAsync(env, 32, Pixiv);
        await env.Tags.SetPostTagsAsync(pixivOnly, TagName.ParseMany("landscape"), Ct);

        var untagged = await CreateWithSourceAsync(env, 33, Pixiv);
        Assert.True(await env.Posts.AddSourceAsync(untagged, Danbooru, Ct));

        // Terms are ANDed, exactly as tag terms are.
        Assert.Equal([both], await FindAsync(env, "landscape url:pixiv.net url:donmai.us"));
        Assert.Equal([pixivOnly], await FindAsync(env, "landscape url:pixiv.net -url:donmai.us"));
        Assert.Empty(await FindAsync(env, "landscape url:tumblr.com"));
    }

    /// <summary>
    /// A required tag nobody has still short-circuits the whole search, even when a source
    /// term would otherwise match plenty.
    /// </summary>
    [Fact]
    public async Task An_unsatisfiable_tag_beats_a_matching_url_term()
    {
        using var env = new TestEnvironment();
        await CreateWithSourceAsync(env, 34, Pixiv);

        Assert.Empty(await FindAsync(env, "url:pixiv.net nonexistent_tag"));
    }

    /// <summary>
    /// Percent and underscore are ordinary characters in a URL. If matching were built on
    /// LIKE without escaping them, "%" would match everything and "_" any single character.
    /// </summary>
    [Fact]
    public async Task Wildcard_characters_in_the_search_text_are_literal()
    {
        using var env = new TestEnvironment();

        var encoded = await CreateWithSourceAsync(env, 35, "https://example.invalid/tag/100%25_done");
        await CreateWithSourceAsync(env, 36, "https://example.invalid/tag/plain");

        Assert.Equal([encoded], await FindAsync(env, "url:100%25_done"));

        // A literal "%" matches only the address that actually contains one — as a wildcard it
        // would have matched the plain post too.
        Assert.Equal([encoded], await FindAsync(env, "url:%"));

        // And "_" matches only itself, not "l" standing in the same position.
        Assert.Empty(await FindAsync(env, "url:tag/p_ain"));
    }

    [Fact]
    public async Task A_random_post_honours_a_url_search()
    {
        using var env = new TestEnvironment();

        var pixiv = await CreateWithSourceAsync(env, 37, Pixiv);
        await CreateWithSourceAsync(env, 38, Danbooru);

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("url:pixiv.net"), Ct);

        // Only one post can satisfy it, so the pick is deterministic.
        Assert.Equal(pixiv, await env.Posts.GetRandomIdAsync(resolved, Ct));
    }
}
