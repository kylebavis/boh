using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class UnusedTagCleanupTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    private static async Task<string[]> RemainingTagsAsync(TestEnvironment env)
    {
        env.Db.ChangeTracker.Clear();
        return (await env.Db.Tags.AsNoTracking().ToListAsync(Ct))
            .Select(t => new TagName(t.Namespace, t.Name).Display)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public async Task A_tag_no_post_carries_is_deleted()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.SetPostTagsAsync(post, Names("keep", "drop"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("keep"), Ct);

        var deleted = await env.Tags.DeleteUnusedTagsAsync(Ct);

        Assert.Equal(1, deleted);
        Assert.Equal(["keep"], await RemainingTagsAsync(env));
    }

    [Fact]
    public async Task Tags_still_on_a_post_are_kept()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("a", "b"), Ct);

        Assert.Equal(0, await env.Tags.DeleteUnusedTagsAsync(Ct));
        Assert.Equal(["a", "b"], await RemainingTagsAsync(env));
    }

    /// <summary>
    /// An alias tag holds a post count of zero by design. Deleting it would cascade to the
    /// TagAlias row and the alias would quietly stop redirecting.
    /// </summary>
    [Fact]
    public async Task An_alias_survives_even_though_nothing_is_tagged_with_it()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddAliasAsync(new TagName("", "scenery"), new TagName("", "landscape"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("landscape"), Ct);

        await env.Tags.DeleteUnusedTagsAsync(Ct);

        Assert.Contains("scenery", await RemainingTagsAsync(env));
        Assert.Equal(1, await env.Db.TagAliases.CountAsync(Ct));

        // The alias must still actually work, not merely exist.
        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("scenery"), Ct);
        Assert.False(resolved.Unsatisfiable);
        Assert.NotEmpty(resolved.Include);
    }

    [Fact]
    public async Task A_canonical_tag_with_no_posts_survives_because_an_alias_points_at_it()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddAliasAsync(new TagName("", "scenery"), new TagName("", "landscape"), Ct);

        await env.Tags.DeleteUnusedTagsAsync(Ct);

        var remaining = await RemainingTagsAsync(env);
        Assert.Contains("landscape", remaining);
        Assert.Contains("scenery", remaining);
    }

    [Fact]
    public async Task Implication_rules_survive_even_when_neither_tag_is_in_use()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("meme", "pondering_my_orb"), new TagName("format", "reaction_image"), Ct);

        await env.Tags.DeleteUnusedTagsAsync(Ct);

        var remaining = await RemainingTagsAsync(env);
        Assert.Contains("meme:pondering_my_orb", remaining);
        Assert.Contains("format:reaction_image", remaining);
        Assert.Equal(1, await env.Db.TagImplications.CountAsync(Ct));
    }

    /// <summary>
    /// The denormalized counter can drift, so deletion reads the link table instead. A stale
    /// non-zero count must not save a tag nothing actually carries.
    /// </summary>
    [Fact]
    public async Task A_stale_non_zero_count_does_not_protect_an_unused_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("orphan"), Ct);
        await env.Tags.SetPostTagsAsync(post, [], Ct);

        var tag = (await env.Tags.FindAsync(new TagName("", "orphan"), Ct))!;
        tag.PostCount = 99;
        await env.Db.SaveChangesAsync(Ct);

        Assert.Equal(1, await env.Tags.DeleteUnusedTagsAsync(Ct));
        Assert.Empty(await RemainingTagsAsync(env));
    }

    [Fact]
    public async Task A_stale_zero_count_does_not_delete_a_tag_that_is_still_in_use()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("busy"), Ct);

        var tag = (await env.Tags.FindAsync(new TagName("", "busy"), Ct))!;
        tag.PostCount = 0;
        await env.Db.SaveChangesAsync(Ct);

        Assert.Equal(0, await env.Tags.DeleteUnusedTagsAsync(Ct));
        Assert.Equal(["busy"], await RemainingTagsAsync(env));
    }

    [Fact]
    public async Task An_implied_tag_counts_as_in_use()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        await env.Tags.DeleteUnusedTagsAsync(Ct);

        Assert.Equal(["child", "parent"], await RemainingTagsAsync(env));
    }

    [Fact]
    public async Task Running_it_on_a_clean_collection_deletes_nothing()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);

        Assert.Equal(0, await env.Tags.DeleteUnusedTagsAsync(Ct));
        Assert.Equal(0, await env.Tags.DeleteUnusedTagsAsync(Ct));
    }

    [Fact]
    public async Task Deleting_a_post_leaves_tags_that_this_then_collects()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("only_here"), Ct);

        await env.Posts.DeleteAsync(post, Ct);

        // The realistic path to an orphan: the post is gone, the tag row is not.
        Assert.Equal(1, await env.Tags.DeleteUnusedTagsAsync(Ct));
        Assert.Empty(await RemainingTagsAsync(env));
    }
}
