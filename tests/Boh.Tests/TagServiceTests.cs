using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class TagServiceTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    // ---- basic tagging -------------------------------------------------

    [Fact]
    public async Task Setting_tags_creates_them_and_counts_them()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.SetPostTagsAsync(post, Names("landscape", "artist:foo"), Ct);

        Assert.Equal(["artist:foo", "landscape"], await env.TagsOnAsync(post));
        Assert.Equal(1, (await env.Tags.FindAsync(new TagName("artist", "foo"), Ct))!.PostCount);
    }

    [Fact]
    public async Task Setting_tags_replaces_the_previous_set_and_adjusts_counts()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.SetPostTagsAsync(post, Names("a", "b"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("b", "c"), Ct);

        Assert.Equal(["b", "c"], await env.TagsOnAsync(post));
        Assert.Equal(0, (await env.Tags.FindAsync(new TagName("", "a"), Ct))!.PostCount);
        Assert.Equal(1, (await env.Tags.FindAsync(new TagName("", "b"), Ct))!.PostCount);
    }

    [Fact]
    public async Task Removing_a_tag_leaves_the_others_alone()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("a", "b"), Ct);

        await env.Tags.RemovePostTagAsync(post, new TagName("", "a"), Ct);

        Assert.Equal(["b"], await env.TagsOnAsync(post));
    }

    // ---- aliases -------------------------------------------------------

    [Fact]
    public async Task An_aliased_tag_is_stored_as_its_canonical_form()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        Assert.IsType<TagLinkResult.Ok>(
            await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "bar"), Ct));

        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        Assert.Equal(["bar"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Creating_an_alias_migrates_posts_that_already_used_it()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "bar"), Ct);

        Assert.Equal(["bar"], await env.TagsOnAsync(post));
        Assert.Equal(0, (await env.Tags.FindAsync(new TagName("", "foo"), Ct))!.PostCount);
        Assert.Equal(1, (await env.Tags.FindAsync(new TagName("", "bar"), Ct))!.PostCount);
    }

    [Fact]
    public async Task Alias_chains_resolve_to_the_end_of_the_chain()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(new TagName("", "b"), new TagName("", "c"), Ct);
        await env.Tags.AddAliasAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);

        Assert.Equal(["c"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task A_self_alias_is_rejected()
    {
        using var env = new TestEnvironment();

        var result = await env.Tags.AddAliasAsync(new TagName("", "a"), new TagName("", "a"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    [Fact]
    public async Task An_alias_loop_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddAliasAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        var result = await env.Tags.AddAliasAsync(new TagName("", "b"), new TagName("", "a"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    // ---- implications --------------------------------------------------

    [Fact]
    public async Task Tagging_with_a_child_materializes_the_parent_as_implied()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        Assert.IsType<TagLinkResult.Ok>(await env.Tags.AddImplicationAsync(
            new TagName("meme", "pondering_my_orb"), new TagName("format", "reaction_image"), Ct));

        await env.Tags.SetPostTagsAsync(post, Names("meme:pondering_my_orb"), Ct);

        Assert.Equal(["format:reaction_image", "meme:pondering_my_orb"], await env.TagsOnAsync(post));
        Assert.Equal(TagSource.Explicit, await env.SourceOfAsync(post, "meme:pondering_my_orb"));
        Assert.Equal(TagSource.Implied, await env.SourceOfAsync(post, "format:reaction_image"));
    }

    [Fact]
    public async Task Implications_apply_transitively()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);
        await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "c"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);

        Assert.Equal(["a", "b", "c"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Removing_the_child_also_removes_the_tag_it_implied()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        await env.Tags.RemovePostTagAsync(post, new TagName("", "child"), Ct);

        Assert.Empty(await env.TagsOnAsync(post));
    }

    /// <summary>
    /// The case that motivates the Source column: a parent added by hand must outlive the
    /// child that also implies it, because it was never derived in the first place.
    /// </summary>
    [Fact]
    public async Task An_explicitly_added_parent_survives_removal_of_the_child()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("parent", "child"), Ct);
        Assert.Equal(TagSource.Explicit, await env.SourceOfAsync(post, "parent"));

        await env.Tags.RemovePostTagAsync(post, new TagName("", "child"), Ct);

        Assert.Equal(["parent"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Adding_an_implication_backfills_posts_that_already_had_the_child()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        Assert.Equal(["child", "parent"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Removing_an_implication_strips_the_tags_it_had_produced()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        var child = (await env.Tags.FindAsync(new TagName("", "child"), Ct))!;
        var parent = (await env.Tags.FindAsync(new TagName("", "parent"), Ct))!;
        await env.Tags.RemoveImplicationAsync(child.Id, parent.Id, Ct);

        Assert.Equal(["child"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task A_self_implication_is_rejected()
    {
        using var env = new TestEnvironment();

        var result = await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "a"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    [Fact]
    public async Task A_cyclic_implication_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        var result = await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "a"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    [Fact]
    public async Task A_longer_implication_cycle_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);
        await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "c"), Ct);

        var result = await env.Tags.AddImplicationAsync(new TagName("", "c"), new TagName("", "a"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    [Fact]
    public async Task A_duplicate_implication_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        var result = await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
    }

    // ---- scoped rebuild ------------------------------------------------

    /// <summary>
    /// The property the scoped rebuild relies on: a post can carry the changed tag by
    /// implication rather than by hand. Selecting only explicitly-tagged posts would miss
    /// this one and leave it with a stale closure.
    /// </summary>
    [Fact]
    public async Task Extending_a_chain_updates_posts_that_hold_the_middle_tag_only_by_implication()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);
        Assert.Equal(TagSource.Implied, await env.SourceOfAsync(post, "b"));

        // The post has "b" implied, never explicitly, yet must still gain "c".
        await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "c"), Ct);

        Assert.Equal(["a", "b", "c"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Rebuilding_after_an_implication_change_leaves_unrelated_posts_alone()
    {
        using var env = new TestEnvironment();
        var related = await env.CreatePostAsync(40);
        var unrelated = await env.CreatePostAsync(41);

        await env.Tags.SetPostTagsAsync(related, Names("a"), Ct);
        await env.Tags.SetPostTagsAsync(unrelated, Names("z"), Ct);

        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        Assert.Equal(["a", "b"], await env.TagsOnAsync(related));
        Assert.Equal(["z"], await env.TagsOnAsync(unrelated));
    }

    /// <summary>
    /// A full rebuild after the scoped one must find nothing left to do — otherwise the
    /// scoped path is silently under-applying.
    /// </summary>
    [Fact]
    public async Task A_scoped_rebuild_leaves_nothing_for_a_full_rebuild_to_fix()
    {
        using var env = new TestEnvironment();
        var first = await env.CreatePostAsync(42);
        var second = await env.CreatePostAsync(43);

        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);
        await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "c"), Ct);
        await env.Tags.SetPostTagsAsync(first, Names("a"), Ct);
        await env.Tags.SetPostTagsAsync(second, Names("b", "unrelated"), Ct);

        var child = (await env.Tags.FindAsync(new TagName("", "a"), Ct))!;
        var parent = (await env.Tags.FindAsync(new TagName("", "b"), Ct))!;
        await env.Tags.RemoveImplicationAsync(child.Id, parent.Id, Ct);

        env.Db.ChangeTracker.Clear();
        Assert.Equal(0, await env.Tags.RebuildAllImpliedAsync(Ct));
    }

    [Fact]
    public async Task Counts_stay_correct_across_a_scoped_rebuild()
    {
        using var env = new TestEnvironment();
        var first = await env.CreatePostAsync(44);
        var second = await env.CreatePostAsync(45);

        await env.Tags.SetPostTagsAsync(first, Names("child"), Ct);
        await env.Tags.SetPostTagsAsync(second, Names("child"), Ct);

        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        env.Db.ChangeTracker.Clear();
        Assert.Equal(2, (await env.Tags.FindAsync(new TagName("", "parent"), Ct))!.PostCount);
    }

    // ---- maintenance ---------------------------------------------------

    [Fact]
    public async Task Recount_repairs_drifted_post_counts()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);

        var tag = (await env.Tags.FindAsync(new TagName("", "a"), Ct))!;
        tag.PostCount = 99;
        await env.Db.SaveChangesAsync(Ct);

        await env.Tags.RecountTagsAsync(Ct);

        env.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await env.Tags.FindAsync(new TagName("", "a"), Ct))!.PostCount);
    }

    [Fact]
    public async Task Implied_tags_are_counted_towards_a_tags_total()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        env.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await env.Tags.FindAsync(new TagName("", "parent"), Ct))!.PostCount);
    }

    // ---- search resolution ---------------------------------------------

    [Fact]
    public async Task Search_resolves_an_aliased_term_to_the_canonical_tag()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "bar"), Ct);
        var bar = (await env.Tags.FindAsync(new TagName("", "bar"), Ct))!;

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("foo"), Ct);

        Assert.False(resolved.Unsatisfiable);
        Assert.Equal([bar.Id], resolved.Include);
    }

    [Fact]
    public async Task Requiring_a_tag_that_does_not_exist_makes_the_search_unsatisfiable()
    {
        using var env = new TestEnvironment();

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("nope"), Ct);

        Assert.True(resolved.Unsatisfiable);
    }

    [Fact]
    public async Task Excluding_a_tag_that_does_not_exist_is_a_no_op()
    {
        using var env = new TestEnvironment();

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("-nope"), Ct);

        Assert.False(resolved.Unsatisfiable);
        Assert.Empty(resolved.Exclude);
    }

    // ---- autocomplete --------------------------------------------------

    [Fact]
    public async Task Autocomplete_matches_a_prefix_and_ranks_by_use()
    {
        using var env = new TestEnvironment();
        var busy = await env.CreatePostAsync(30);
        var quiet = await env.CreatePostAsync(31);

        await env.Tags.SetPostTagsAsync(busy, Names("landscape", "lamp"), Ct);
        await env.Tags.SetPostTagsAsync(quiet, Names("landscape"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("la", 10, Ct);

        Assert.Equal("landscape", suggestions[0].Display);
        Assert.Equal(2, suggestions[0].PostCount);
        Assert.Contains(suggestions, s => s.Display == "lamp");
    }

    [Fact]
    public async Task Autocomplete_scopes_to_a_namespace_when_one_is_typed()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("artist:alice", "character:alice"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("artist:al", 10, Ct);

        Assert.Single(suggestions);
        Assert.Equal("artist:alice", suggestions[0].Display);
    }

    [Fact]
    public async Task Autocomplete_reports_where_an_alias_leads()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "foobar"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("foo", 10, Ct);

        var alias = suggestions.Single(s => s.Display == "foo");
        Assert.Equal("foobar", alias.AliasOf);
    }

    [Fact]
    public async Task Autocomplete_returns_nothing_for_blank_input()
    {
        using var env = new TestEnvironment();

        Assert.Empty(await env.Tags.AutocompleteAsync("   ", 10, Ct));
        Assert.Empty(await env.Tags.AutocompleteAsync(null, 10, Ct));
    }
}
