using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class TagMoveTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    [Fact]
    public async Task Moving_a_tag_into_a_namespace_keeps_it_on_the_post()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        var result = await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        Assert.IsType<TagLinkResult.Ok>(result);
        Assert.Equal(["artist:foo"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task A_plain_rename_works_too()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("typoo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "typoo"), new TagName("", "typo"), Ct);

        Assert.Equal(["typo"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Moving_a_tag_that_does_not_exist_is_rejected()
    {
        using var env = new TestEnvironment();

        var result = await env.Tags.MoveTagAsync(new TagName("", "nope"), new TagName("artist", "nope"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    [Fact]
    public async Task Moving_a_tag_onto_itself_is_rejected()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        var result = await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("", "foo"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    // ---- merging into an existing destination ---------------------------

    [Fact]
    public async Task Merging_unions_the_posts_of_both_tags()
    {
        using var env = new TestEnvironment();
        var onlySource = await env.CreatePostAsync(50);
        var both = await env.CreatePostAsync(51);
        var onlyDestination = await env.CreatePostAsync(52);

        await env.Tags.SetPostTagsAsync(onlySource, Names("foo"), Ct);
        await env.Tags.SetPostTagsAsync(both, Names("foo", "artist:foo"), Ct);
        await env.Tags.SetPostTagsAsync(onlyDestination, Names("artist:foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        Assert.Equal(["artist:foo"], await env.TagsOnAsync(onlySource));
        Assert.Equal(["artist:foo"], await env.TagsOnAsync(both));
        Assert.Equal(["artist:foo"], await env.TagsOnAsync(onlyDestination));

        env.Db.ChangeTracker.Clear();
        Assert.Null(await env.Tags.FindAsync(new TagName("", "foo"), Ct));
        Assert.Equal(3, (await env.Tags.FindAsync(new TagName("artist", "foo"), Ct))!.PostCount);
    }

    /// <summary>
    /// A post can hold the destination as implied while holding the source explicitly.
    /// The merge must not quietly demote the hand-typed one.
    /// </summary>
    [Fact]
    public async Task Merging_keeps_the_stronger_provenance()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("artist", "foo"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("child", "foo"), Ct);

        Assert.Equal(TagSource.Implied, await env.SourceOfAsync(post, "artist:foo"));
        Assert.Equal(TagSource.Explicit, await env.SourceOfAsync(post, "foo"));

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        Assert.Equal(TagSource.Explicit, await env.SourceOfAsync(post, "artist:foo"));
    }

    [Fact]
    public async Task Merging_carries_over_an_alias_pointing_at_the_source()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddAliasAsync(new TagName("", "eff"), new TagName("", "foo"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("artist:foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        // The alias must now resolve to the merged destination, not dangle.
        await env.Tags.SetPostTagsAsync(post, Names("eff"), Ct);
        Assert.Equal(["artist:foo"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Merging_carries_over_an_implication_on_the_source()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "foo"), new TagName("series", "bar"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("artist:foo"), Ct);
        Assert.Equal(["artist:foo", "series:bar"], await env.TagsOnAsync(post));
    }

    /// <summary>
    /// Merging a tag into one it already implies would leave an edge from a tag to itself,
    /// which the closure walk treats as a cycle.
    /// </summary>
    [Fact]
    public async Task Merging_two_tags_joined_by_an_implication_leaves_no_self_edge()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        env.Db.ChangeTracker.Clear();
        Assert.Empty(await env.Db.TagImplications.Where(i => i.ChildTagId == i.ParentTagId).ToListAsync(Ct));
    }

    [Fact]
    public async Task Merging_leaves_no_orphaned_link_rows()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo", "artist:foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        env.Db.ChangeTracker.Clear();
        var tagIds = await env.Db.Tags.Select(t => t.Id).ToListAsync(Ct);
        var linkTagIds = await env.Db.PostTags.Select(pt => pt.TagId).Distinct().ToListAsync(Ct);

        Assert.All(linkTagIds, id => Assert.Contains(id, tagIds));
    }

    [Fact]
    public async Task The_moved_tag_is_findable_under_its_new_name_and_gone_from_the_old()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        env.Db.ChangeTracker.Clear();
        Assert.NotNull(await env.Tags.FindAsync(new TagName("artist", "foo"), Ct));
        Assert.Null(await env.Tags.FindAsync(new TagName("", "foo"), Ct));

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("artist:foo"), Ct);
        Assert.False(resolved.Unsatisfiable);
    }
}
