using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class TagMoveTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    [Test]
    public async Task Moving_a_tag_into_a_namespace_keeps_it_on_the_post()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        var result = await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Ok>();
        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["artist:foo"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_plain_rename_works_too()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("typoo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "typoo"), new TagName("", "typo"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["typo"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Moving_a_tag_that_does_not_exist_is_rejected()
    {
        using var env = new TestEnvironment();

        var result = await env.Tags.MoveTagAsync(new TagName("", "nope"), new TagName("artist", "nope"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    public async Task Moving_a_tag_onto_itself_is_rejected()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        var result = await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("", "foo"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    // ---- merging into an existing destination ---------------------------

    [Test]
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

        await Assert.That(await env.TagsOnAsync(onlySource)).IsEquivalentTo(["artist:foo"], CollectionOrdering.Matching);
        await Assert.That(await env.TagsOnAsync(both)).IsEquivalentTo(["artist:foo"], CollectionOrdering.Matching);
        await Assert.That(await env.TagsOnAsync(onlyDestination)).IsEquivalentTo(["artist:foo"], CollectionOrdering.Matching);

        env.Db.ChangeTracker.Clear();
        await Assert.That(await env.Tags.FindAsync(new TagName("", "foo"), Ct)).IsNull();
        await Assert.That((await env.Tags.FindAsync(new TagName("artist", "foo"), Ct))!.PostCount).IsEqualTo(3);
    }

    /// <summary>
    /// A post can hold the destination as implied while holding the source explicitly.
    /// The merge must not quietly demote the hand-typed one.
    /// </summary>
    [Test]
    public async Task Merging_keeps_the_stronger_provenance()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("artist", "foo"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("child", "foo"), Ct);

        await Assert.That(await env.SourceOfAsync(post, "artist:foo")).IsEqualTo(TagSource.Implied);
        await Assert.That(await env.SourceOfAsync(post, "foo")).IsEqualTo(TagSource.Explicit);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        await Assert.That(await env.SourceOfAsync(post, "artist:foo")).IsEqualTo(TagSource.Explicit);
    }

    [Test]
    public async Task Merging_carries_over_an_alias_pointing_at_the_source()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddAliasAsync(new TagName("", "eff"), new TagName("", "foo"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("artist:foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        // The alias must now resolve to the merged destination, not dangle.
        await env.Tags.SetPostTagsAsync(post, Names("eff"), Ct);
        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["artist:foo"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Merging_an_alias_into_a_used_tag_migrates_that_tags_posts()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddAliasAsync(new TagName("", "eff"), new TagName("", "foo"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("artist:eff"), Ct);

        // artist:eff inherits the alias, so its post belongs on foo.
        await env.Tags.MoveTagAsync(new TagName("", "eff"), new TagName("artist", "eff"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["foo"], CollectionOrdering.Matching);
        await Assert.That((await env.Tags.FindAsync(new TagName("", "foo"), Ct))!.PostCount).IsEqualTo(1);
    }

    [Test]
    public async Task Merging_into_an_alias_lands_posts_on_its_canonical_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddAliasAsync(new TagName("artist", "eff"), new TagName("", "foo"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("eff"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "eff"), new TagName("artist", "eff"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["foo"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Merging_carries_over_an_implication_on_the_source()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "foo"), new TagName("series", "bar"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("artist:foo"), Ct);
        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["artist:foo", "series:bar"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Merging a tag into one it already implies would leave an edge from a tag to itself,
    /// which the closure walk treats as a cycle.
    /// </summary>
    [Test]
    public async Task Merging_two_tags_joined_by_an_implication_leaves_no_self_edge()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        env.Db.ChangeTracker.Clear();
        await Assert.That(await env.Db.TagImplications.Where(i => i.ChildTagId == i.ParentTagId).ToListAsync(Ct)).IsEmpty();
    }

    [Test]
    public async Task Merging_leaves_no_orphaned_link_rows()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo", "artist:foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        env.Db.ChangeTracker.Clear();
        var tagIds = await env.Db.Tags.Select(t => t.Id).ToListAsync(Ct);
        var linkTagIds = await env.Db.PostTags.Select(pt => pt.TagId).Distinct().ToListAsync(Ct);

        await Assert.That(linkTagIds).All(id => tagIds.Contains(id));
    }

    [Test]
    public async Task The_moved_tag_is_findable_under_its_new_name_and_gone_from_the_old()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        await env.Tags.MoveTagAsync(new TagName("", "foo"), new TagName("artist", "foo"), Ct);

        env.Db.ChangeTracker.Clear();
        await Assert.That(await env.Tags.FindAsync(new TagName("artist", "foo"), Ct)).IsNotNull();
        await Assert.That(await env.Tags.FindAsync(new TagName("", "foo"), Ct)).IsNull();

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("artist:foo"), Ct);
        await Assert.That(resolved.Unsatisfiable).IsFalse();
    }
}
