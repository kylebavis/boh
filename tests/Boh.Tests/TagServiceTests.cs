using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class TagServiceTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    // ---- basic tagging -------------------------------------------------

    [Test]
    public async Task Setting_tags_creates_them_and_counts_them()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.SetPostTagsAsync(post, Names("landscape", "artist:foo"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["artist:foo", "landscape"], CollectionOrdering.Matching);
        await Assert.That((await env.Tags.FindAsync(new TagName("artist", "foo"), Ct))!.PostCount).IsEqualTo(1);
    }

    [Test]
    public async Task Setting_tags_replaces_the_previous_set_and_adjusts_counts()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.SetPostTagsAsync(post, Names("a", "b"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("b", "c"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["b", "c"], CollectionOrdering.Matching);
        await Assert.That((await env.Tags.FindAsync(new TagName("", "a"), Ct))!.PostCount).IsEqualTo(0);
        await Assert.That((await env.Tags.FindAsync(new TagName("", "b"), Ct))!.PostCount).IsEqualTo(1);
    }

    [Test]
    public async Task Removing_a_tag_leaves_the_others_alone()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("a", "b"), Ct);

        await env.Tags.RemovePostTagAsync(post, new TagName("", "a"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["b"], CollectionOrdering.Matching);
    }

    // ---- aliases -------------------------------------------------------

    [Test]
    public async Task An_aliased_tag_is_stored_as_its_canonical_form()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await Assert.That(await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "bar"), Ct)).IsTypeOf<TagLinkResult.Ok>();

        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["bar"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Creating_an_alias_migrates_posts_that_already_used_it()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "bar"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["bar"], CollectionOrdering.Matching);
        await Assert.That((await env.Tags.FindAsync(new TagName("", "foo"), Ct))!.PostCount).IsEqualTo(0);
        await Assert.That((await env.Tags.FindAsync(new TagName("", "bar"), Ct))!.PostCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_migration_leaves_no_alias_behind()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("foo"), Ct);

        await env.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail BEFORE INSERT ON PostTags " +
            "WHEN NEW.TagId = (SELECT Id FROM Tags WHERE Name = 'bar') " +
            "BEGIN SELECT RAISE(ABORT, 'boom'); END",
            cancellationToken: Ct);

        await Assert.That(async () => { await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "bar"), Ct); }).Throws<Exception>();

        env.Db.ChangeTracker.Clear();
        await Assert.That(await env.Db.TagAliases.AnyAsync(Ct)).IsFalse();
        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["foo"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Alias_chains_resolve_to_the_end_of_the_chain()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(new TagName("", "b"), new TagName("", "c"), Ct);
        await env.Tags.AddAliasAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["c"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_self_alias_is_rejected()
    {
        using var env = new TestEnvironment();

        var result = await env.Tags.AddAliasAsync(new TagName("", "a"), new TagName("", "a"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    public async Task An_alias_loop_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddAliasAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        var result = await env.Tags.AddAliasAsync(new TagName("", "b"), new TagName("", "a"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    // ---- implications --------------------------------------------------

    [Test]
    public async Task Tagging_with_a_child_materializes_the_parent_as_implied()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await Assert.That(await env.Tags.AddImplicationAsync(
            new TagName("meme", "pondering_my_orb"), new TagName("format", "reaction_image"), Ct)).IsTypeOf<TagLinkResult.Ok>();

        await env.Tags.SetPostTagsAsync(post, Names("meme:pondering_my_orb"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["format:reaction_image", "meme:pondering_my_orb"], CollectionOrdering.Matching);
        await Assert.That(await env.SourceOfAsync(post, "meme:pondering_my_orb")).IsEqualTo(TagSource.Explicit);
        await Assert.That(await env.SourceOfAsync(post, "format:reaction_image")).IsEqualTo(TagSource.Implied);
    }

    [Test]
    public async Task Implications_apply_transitively()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);
        await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "c"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Removing_the_child_also_removes_the_tag_it_implied()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        await env.Tags.RemovePostTagAsync(post, new TagName("", "child"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEmpty();
    }

    /// <summary>
    /// The case that motivates the Source column: a parent added by hand must outlive the
    /// child that also implies it, because it was never derived in the first place.
    /// </summary>
    [Test]
    public async Task An_explicitly_added_parent_survives_removal_of_the_child()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("parent", "child"), Ct);
        await Assert.That(await env.SourceOfAsync(post, "parent")).IsEqualTo(TagSource.Explicit);

        await env.Tags.RemovePostTagAsync(post, new TagName("", "child"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["parent"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Adding_an_implication_backfills_posts_that_already_had_the_child()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["child", "parent"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// An implication is stored canonically, but aliasing its parent afterwards leaves the
    /// edge pointing at what is now an alias. An implied row was the one way an aliased tag
    /// could still land on a post — most visibly on import, where every tag arrives derived.
    /// </summary>
    [Test]
    public async Task An_implied_tag_aliased_after_the_edge_was_added_resolves()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "cat"), new TagName("", "feline"), Ct);
        await env.Tags.AddAliasAsync(new TagName("", "feline"), new TagName("", "cat_species"), Ct);

        await env.Tags.AddPostTagsAsync(post, Names("cat"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["cat", "cat_species"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Aliasing_an_implied_tag_migrates_posts_that_already_derived_it()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "cat"), new TagName("", "feline"), Ct);
        await env.Tags.AddPostTagsAsync(post, Names("cat"), Ct);

        await env.Tags.AddAliasAsync(new TagName("", "feline"), new TagName("", "cat_species"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["cat", "cat_species"], CollectionOrdering.Matching);
        await Assert.That(await env.SourceOfAsync(post, "cat_species")).IsEqualTo(TagSource.Implied);
    }

    /// <summary>
    /// Redirecting a tag must not sever the chain above it: edges stored against the alias
    /// are still real implications and have to keep contributing their own ancestors.
    /// </summary>
    [Test]
    public async Task An_aliased_tag_still_contributes_the_tags_it_implied()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "cat"), new TagName("", "feline"), Ct);
        await env.Tags.AddImplicationAsync(new TagName("", "feline"), new TagName("", "animal"), Ct);
        await env.Tags.AddAliasAsync(new TagName("", "feline"), new TagName("", "cat_species"), Ct);

        await env.Tags.AddPostTagsAsync(post, Names("cat"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["animal", "cat", "cat_species"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Removing_an_implication_strips_the_tags_it_had_produced()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        var child = (await env.Tags.FindAsync(new TagName("", "child"), Ct))!;
        var parent = (await env.Tags.FindAsync(new TagName("", "parent"), Ct))!;
        await env.Tags.RemoveImplicationAsync(child.Id, parent.Id, Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["child"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_self_implication_is_rejected()
    {
        using var env = new TestEnvironment();

        var result = await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "a"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    public async Task A_cyclic_implication_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        var result = await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "a"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    public async Task A_longer_implication_cycle_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);
        await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "c"), Ct);

        var result = await env.Tags.AddImplicationAsync(new TagName("", "c"), new TagName("", "a"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    public async Task A_duplicate_implication_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        var result = await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
    }

    // ---- scoped rebuild ------------------------------------------------

    /// <summary>
    /// The property the scoped rebuild relies on: a post can carry the changed tag by
    /// implication rather than by hand. Selecting only explicitly-tagged posts would miss
    /// this one and leave it with a stale closure.
    /// </summary>
    [Test]
    public async Task Extending_a_chain_updates_posts_that_hold_the_middle_tag_only_by_implication()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("a"), Ct);
        await Assert.That(await env.SourceOfAsync(post, "b")).IsEqualTo(TagSource.Implied);

        // The post has "b" implied, never explicitly, yet must still gain "c".
        await env.Tags.AddImplicationAsync(new TagName("", "b"), new TagName("", "c"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rebuilding_after_an_implication_change_leaves_unrelated_posts_alone()
    {
        using var env = new TestEnvironment();
        var related = await env.CreatePostAsync(40);
        var unrelated = await env.CreatePostAsync(41);

        await env.Tags.SetPostTagsAsync(related, Names("a"), Ct);
        await env.Tags.SetPostTagsAsync(unrelated, Names("z"), Ct);

        await env.Tags.AddImplicationAsync(new TagName("", "a"), new TagName("", "b"), Ct);

        await Assert.That(await env.TagsOnAsync(related)).IsEquivalentTo(["a", "b"], CollectionOrdering.Matching);
        await Assert.That(await env.TagsOnAsync(unrelated)).IsEquivalentTo(["z"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// A full rebuild after the scoped one must find nothing left to do — otherwise the
    /// scoped path is silently under-applying.
    /// </summary>
    [Test]
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
        await Assert.That(await env.Tags.RebuildAllImpliedAsync(Ct)).IsEqualTo(0);
    }

    [Test]
    public async Task Counts_stay_correct_across_a_scoped_rebuild()
    {
        using var env = new TestEnvironment();
        var first = await env.CreatePostAsync(44);
        var second = await env.CreatePostAsync(45);

        await env.Tags.SetPostTagsAsync(first, Names("child"), Ct);
        await env.Tags.SetPostTagsAsync(second, Names("child"), Ct);

        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        env.Db.ChangeTracker.Clear();
        await Assert.That((await env.Tags.FindAsync(new TagName("", "parent"), Ct))!.PostCount).IsEqualTo(2);
    }

    // ---- maintenance ---------------------------------------------------

    [Test]
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
        await Assert.That((await env.Tags.FindAsync(new TagName("", "a"), Ct))!.PostCount).IsEqualTo(1);
    }

    [Test]
    public async Task Implied_tags_are_counted_towards_a_tags_total()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.AddImplicationAsync(new TagName("", "child"), new TagName("", "parent"), Ct);

        await env.Tags.SetPostTagsAsync(post, Names("child"), Ct);

        env.Db.ChangeTracker.Clear();
        await Assert.That((await env.Tags.FindAsync(new TagName("", "parent"), Ct))!.PostCount).IsEqualTo(1);
    }

    // ---- search resolution ---------------------------------------------

    [Test]
    public async Task Search_resolves_an_aliased_term_to_the_canonical_tag()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "bar"), Ct);
        var bar = (await env.Tags.FindAsync(new TagName("", "bar"), Ct))!;

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("foo"), Ct);

        await Assert.That(resolved.Unsatisfiable).IsFalse();
        await Assert.That(resolved.Include).IsEquivalentTo([bar.Id], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Requiring_a_tag_that_does_not_exist_makes_the_search_unsatisfiable()
    {
        using var env = new TestEnvironment();

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("nope"), Ct);

        await Assert.That(resolved.Unsatisfiable).IsTrue();
    }

    [Test]
    public async Task Excluding_a_tag_that_does_not_exist_is_a_no_op()
    {
        using var env = new TestEnvironment();

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("-nope"), Ct);

        await Assert.That(resolved.Unsatisfiable).IsFalse();
        await Assert.That(resolved.Exclude).IsEmpty();
    }

    // ---- autocomplete --------------------------------------------------

    [Test]
    public async Task Autocomplete_matches_a_prefix_and_ranks_by_use()
    {
        using var env = new TestEnvironment();
        var busy = await env.CreatePostAsync(30);
        var quiet = await env.CreatePostAsync(31);

        await env.Tags.SetPostTagsAsync(busy, Names("landscape", "lamp"), Ct);
        await env.Tags.SetPostTagsAsync(quiet, Names("landscape"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("la", 10, Ct);

        await Assert.That(suggestions[0].Display).IsEqualTo("landscape");
        await Assert.That(suggestions[0].PostCount).IsEqualTo(2);
        await Assert.That(suggestions).Contains(s => s.Display == "lamp");
    }

    [Test]
    public async Task Autocomplete_scopes_to_a_namespace_when_one_is_typed()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("artist:alice", "character:alice"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("artist:al", 10, Ct);

        await Assert.That(suggestions).HasSingleItem();
        await Assert.That(suggestions[0].Display).IsEqualTo("artist:alice");
    }

    /// <summary>The match is a range comparison, so its edges and non-ASCII text need checking.</summary>
    [Test]
    public async Task Autocomplete_matches_exactly_the_tags_that_start_with_the_prefix()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post,
            Names("caf", "café", "café𠀀", "cafe", "cag", "ca", "artist:cafeteria", "cafx:other"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("CAF", 10, Ct);

        await Assert.That(suggestions.Select(s => s.Display).Order(StringComparer.Ordinal)).IsEquivalentTo(["artist:cafeteria", "caf", "cafe", "cafx:other", "café", "café𠀀"], CollectionOrdering.Matching);

        var accented = await env.Tags.AutocompleteAsync("café", 10, Ct);
        await Assert.That(accented.Select(s => s.Display).Order(StringComparer.Ordinal)).IsEquivalentTo(["café", "café𠀀"], CollectionOrdering.Matching);
    }

    private static async Task SetCountsAsync(TestEnvironment env, params (string Name, int Count)[] tags)
    {
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names(tags.Select(t => t.Name).ToArray()), Ct);

        foreach (var (name, count) in tags)
        {
            await Assert.That(TagName.TryParse(name, out var tag)).IsTrue();
            await env.Db.Tags.Where(t => t.Namespace == tag.Namespace && t.Name == tag.Name)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PostCount, count), Ct);
        }
    }

    [Test]
    public async Task Autocomplete_ranks_prefix_then_word_start_then_anywhere()
    {
        using var env = new TestEnvironment();
        await SetCountsAsync(env,
            ("absorb", 100), ("pondering_my_orb", 50), ("glowing-orb", 5), ("orbit", 1), ("lamp", 200));

        var suggestions = await env.Tags.AutocompleteAsync("orb", 10, Ct);

        await Assert.That(suggestions.Select(s => s.Display)).IsEquivalentTo(["orbit", "pondering_my_orb", "glowing-orb", "absorb"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Autocomplete_does_not_look_inside_names_when_prefixes_fill_the_list()
    {
        using var env = new TestEnvironment();
        await SetCountsAsync(env, ("orbit", 1), ("orbs", 2), ("pondering_my_orb", 50));

        var suggestions = await env.Tags.AutocompleteAsync("orb", 2, Ct);

        await Assert.That(suggestions.Select(s => s.Display)).IsEquivalentTo(["orbs", "orbit"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Autocomplete_looks_inside_names_within_the_typed_namespace()
    {
        using var env = new TestEnvironment();
        await SetCountsAsync(env, ("meme:pondering_my_orb", 1), ("artist:orb_painter", 1), ("absorb", 1));

        var suggestions = await env.Tags.AutocompleteAsync("meme:orb", 10, Ct);

        await Assert.That(suggestions.Select(s => s.Display)).IsEquivalentTo(["meme:pondering_my_orb"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Autocomplete_reports_where_an_alias_leads()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddAliasAsync(new TagName("", "foo"), new TagName("", "foobar"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("foo", 10, Ct);

        var alias = suggestions.Single(s => s.Display == "foo");
        await Assert.That(alias.AliasOf).IsEqualTo("foobar");
    }

    [Test]
    public async Task Autocomplete_returns_nothing_for_blank_input()
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Tags.AutocompleteAsync("   ", 10, Ct)).IsEmpty();
        await Assert.That(await env.Tags.AutocompleteAsync(null, 10, Ct)).IsEmpty();
    }
}
