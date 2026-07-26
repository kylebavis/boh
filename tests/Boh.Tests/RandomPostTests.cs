using Boh.Web.Tags;

namespace Boh.Tests;

public class RandomPostTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    [Fact]
    public async Task Returns_null_when_there_are_no_posts()
    {
        using var env = new TestEnvironment();

        Assert.Null(await env.Posts.GetRandomIdAsync(null, Ct));
    }

    [Fact]
    public async Task Returns_the_only_post_when_there_is_one()
    {
        using var env = new TestEnvironment();
        var id = await env.CreatePostAsync();

        Assert.Equal(id, await env.Posts.GetRandomIdAsync(null, Ct));
    }

    [Fact]
    public async Task Only_ever_returns_a_real_post_id()
    {
        using var env = new TestEnvironment();
        var ids = new HashSet<int>
        {
            await env.CreatePostAsync(40),
            await env.CreatePostAsync(41),
            await env.CreatePostAsync(42),
        };

        for (var i = 0; i < 30; i++)
        {
            var picked = await env.Posts.GetRandomIdAsync(null, Ct);
            Assert.NotNull(picked);
            Assert.Contains(picked!.Value, ids);
        }
    }

    /// <summary>
    /// The offset arithmetic is the part that can silently go wrong: an off-by-one would make
    /// one end of the collection unreachable, which no single call would reveal.
    /// </summary>
    [Fact]
    public async Task Every_post_is_reachable_including_the_first_and_last()
    {
        using var env = new TestEnvironment();
        var ids = new List<int>
        {
            await env.CreatePostAsync(43),
            await env.CreatePostAsync(44),
            await env.CreatePostAsync(45),
        };

        var seen = new HashSet<int>();
        for (var i = 0; i < 200 && seen.Count < ids.Count; i++)
        {
            var picked = await env.Posts.GetRandomIdAsync(null, Ct);
            if (picked is not null) seen.Add(picked.Value);
        }

        Assert.Equal(ids.ToHashSet(), seen);
    }

    [Fact]
    public async Task Honours_the_active_search()
    {
        using var env = new TestEnvironment();
        var wanted = await env.CreatePostAsync(46);
        var other = await env.CreatePostAsync(47);

        await env.Tags.SetPostTagsAsync(wanted, Names("keeper"), Ct);
        await env.Tags.SetPostTagsAsync(other, Names("elsewhere"), Ct);

        var search = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("keeper"), Ct);

        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(wanted, await env.Posts.GetRandomIdAsync(search, Ct));
        }
    }

    [Fact]
    public async Task Returns_null_when_the_search_matches_nothing()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("something"), Ct);

        var search = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("no_such_tag"), Ct);

        Assert.Null(await env.Posts.GetRandomIdAsync(search, Ct));
    }

    [Fact]
    public async Task An_exclusion_can_leave_nothing_to_pick()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("only_tag"), Ct);

        var search = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("-only_tag"), Ct);

        Assert.Null(await env.Posts.GetRandomIdAsync(search, Ct));
    }
}
