using Boh.Web.Services;
using Boh.Web.Tags;

namespace Boh.Tests;

public class TagNamespaceAliasTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    // ---- redirecting new tags ------------------------------------------

    [Test]
    public async Task Tagging_in_an_aliased_namespace_stores_the_canonical_namespace()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct)).IsNull();
    }

    /// <summary>The whole point: a title nobody has aliased individually still lands right.</summary>
    [Test]
    public async Task The_redirect_applies_to_names_that_never_existed_before()
    {
        using var env = new TestEnvironment();
        var first = await env.CreatePostAsync(32);
        var second = await env.CreatePostAsync(33);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await env.Tags.SetPostTagsAsync(first, Names("copyright:brand_new_show"), Ct);
        await env.Tags.SetPostTagsAsync(second, Names("copyright:another_one"), Ct);

        await Assert.That(await env.TagsOnAsync(first)).IsEquivalentTo(["series:brand_new_show"], CollectionOrdering.Matching);
        await Assert.That(await env.TagsOnAsync(second)).IsEquivalentTo(["series:another_one"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tags_in_other_namespaces_are_untouched()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("artist:foo", "landscape", "copyright:x"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["artist:foo", "landscape", "series:x"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Both_spellings_on_one_post_collapse_to_a_single_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars", "series:star_wars"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That((await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.PostCount).IsEqualTo(1);
    }

    // ---- migrating what is already there -------------------------------

    [Test]
    public async Task Creating_the_alias_moves_tags_already_in_that_namespace()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct)).IsNull();
        await Assert.That((await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.PostCount).IsEqualTo(1);
    }

    [Test]
    public async Task Migration_merges_into_a_name_the_destination_already_has()
    {
        using var env = new TestEnvironment();
        var onlyOld = await env.CreatePostAsync(32);
        var both = await env.CreatePostAsync(33);
        var onlyNew = await env.CreatePostAsync(34);

        await env.Tags.SetPostTagsAsync(onlyOld, Names("copyright:star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(both, Names("copyright:star_wars", "series:star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(onlyNew, Names("series:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await Assert.That(await env.TagsOnAsync(onlyOld)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(await env.TagsOnAsync(both)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(await env.TagsOnAsync(onlyNew)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);

        await Assert.That(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct)).IsNull();
        await Assert.That((await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.PostCount).IsEqualTo(3);
    }

    /// <summary>
    /// A merge clears the change tracker, so a migration holding entities across iterations
    /// would quietly stop saving after the first merge.
    /// </summary>
    [Test]
    public async Task Migration_handles_several_tags_including_a_merge()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.SetPostTagsAsync(
            post, Names("copyright:aaa", "copyright:bbb", "copyright:ccc", "series:bbb"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:aaa", "series:bbb", "series:ccc"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Migration_keeps_aliases_and_implications_pointing_at_the_merged_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("copyright", "star_wars"), new TagName("", "scifi"), Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await env.Tags.SetPostTagsAsync(post, Names("series:star_wars"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["scifi", "series:star_wars"], CollectionOrdering.Matching);
    }

    // ---- meeting tag aliases that were added one at a time --------------

    /// <summary>
    /// The migration path this feature exists to replace: a per-tag alias saying exactly what
    /// the namespace alias now says. Merging the alias tag into its own canonical tag leaves
    /// the redirect self-referential, and a self-referential alias is dropped.
    /// </summary>
    [Test]
    public async Task A_tag_alias_the_namespace_alias_makes_redundant_is_cleaned_up()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(
            new TagName("copyright", "star_wars"), new TagName("series", "star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(env.Db.TagAliases).IsEmpty();
        await Assert.That(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct)).IsNull();
    }

    /// <summary>
    /// A per-tag alias that also corrects the <em>name</em> is not made redundant, so it has
    /// to survive the namespace move rather than being swept up with the redundant ones.
    /// </summary>
    [Test]
    public async Task A_tag_alias_that_also_renames_survives_and_moves_namespace()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(
            new TagName("copyright", "sw"), new TagName("series", "star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        // The nickname now lives in the canonical namespace and still redirects.
        await env.Tags.SetPostTagsAsync(post, Names("series:sw"), Ct);
        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);

        // And reaching it by the old namespace still works, via the namespace alias.
        await env.Tags.SetPostTagsAsync(post, Names("copyright:sw"), Ct);
        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
    }

    /// <summary>A tag alias pointing the opposite way is subsumed rather than left fighting it.</summary>
    [Test]
    public async Task A_tag_alias_pointing_the_other_way_is_subsumed()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(
            new TagName("series", "star_wars"), new TagName("copyright", "star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("series:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(env.Db.TagAliases).IsEmpty();
    }

    [Test]
    public async Task Adding_the_now_redundant_tag_alias_afterwards_is_rejected_not_duplicated()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        var result = await env.Tags.AddAliasAsync(
            new TagName("copyright", "star_wars"), new TagName("series", "star_wars"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Rejected>();
        await Assert.That(env.Db.TagAliases).IsEmpty();
    }

    // ---- search and autocomplete ---------------------------------------

    [Test]
    public async Task Searching_an_aliased_namespace_finds_the_canonical_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("copyright:star_wars"), Ct);

        await Assert.That(resolved.Unsatisfiable).IsFalse();
        var canonical = (await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.Id;
        await Assert.That(resolved.Include).IsEquivalentTo([canonical], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Autocomplete_on_an_aliased_namespace_offers_the_canonical_tags()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("copyright:star", 10, Ct);

        await Assert.That(suggestions.Select(s => s.Display)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
    }

    // ---- interaction with moves ----------------------------------------

    [Test]
    public async Task Moving_a_tag_into_an_aliased_namespace_lands_in_the_canonical_one()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        var result = await env.Tags.MoveTagAsync(
            new TagName("", "star_wars"), new TagName("copyright", "star_wars"), Ct);

        await Assert.That(result).IsTypeOf<TagLinkResult.Ok>();
        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
    }

    // ---- chains and validation -----------------------------------------

    [Test]
    public async Task Namespace_alias_chains_resolve_to_the_end_of_the_chain()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("franchise", "series", Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "franchise", Ct);

        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Adding_an_alias_migrates_through_an_existing_chain()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("franchise", "series", Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "franchise", Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_self_alias_is_rejected()
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Tags.AddNamespaceAliasAsync("series", "series", Ct)).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    public async Task A_namespace_alias_loop_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await Assert.That(await env.Tags.AddNamespaceAliasAsync("series", "copyright", Ct)).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    public async Task Aliasing_a_namespace_twice_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await Assert.That(await env.Tags.AddNamespaceAliasAsync("copyright", "franchise", Ct)).IsTypeOf<TagLinkResult.Rejected>();
    }

    [Test]
    [Arguments("", "series")]
    [Arguments("   ", "series")]
    [Arguments("copyright", "")]
    [Arguments("has space", "series")]
    [Arguments("copyright", "no:colons")]
    // A namespace reaches the log once it is accepted, so a line break in an accepted one
    // would let a forged log entry be written. It cannot be accepted in the first place.
    [Arguments("copy\nright", "series")]
    [Arguments("copyright", "ser\ries")]
    [Arguments("copyright\r\nfatal: forged", "series")]
    [Arguments("copyright\0", "series")]
    public async Task An_unusable_namespace_is_rejected(string alias, string canonical)
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Tags.AddNamespaceAliasAsync(alias, canonical, Ct)).IsTypeOf<TagLinkResult.Rejected>();
    }

    /// <summary>
    /// The property behind the log-forging question: whatever a namespace alias is asked to
    /// be, what actually gets stored — and therefore logged — is only ever <c>[a-z0-9_]</c>.
    /// </summary>
    [Test]
    [Arguments("copyright")]
    [Arguments("  COPYRIGHT  ")]
    [Arguments("copy\nright")]
    [Arguments("copyright\r\nfatal: forged")]
    [Arguments("copy right")]
    [Arguments("copy:right")]
    [Arguments("copy-right")]
    [Arguments("copyright\0")]
    [Arguments("copy\tright")]
    [Arguments("\u00a9opyright")]
    [Arguments("copy\u2028right")]
    [Arguments("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task An_accepted_namespace_is_always_plain_alphanumeric(string attempt)
    {
        using var env = new TestEnvironment();

        await env.Tags.AddNamespaceAliasAsync(attempt, "series", Ct);

        // Whether it was accepted or rejected, anything that reached storage \u2014 and so can
        // reach a log line \u2014 is plain and short.
        var stored = await env.Tags.GetNamespaceAliasesAsync(Ct);

        foreach (var value in stored.SelectMany(a => new[] { a.Key, a.Value }))
        {
            await Assert.That(value).Matches("^[a-z0-9_]{1,32}$");
        }
    }

    [Test]
    public async Task A_namespace_alias_is_normalized_before_it_is_stored()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("  COPYRIGHT ", "Series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(await env.Tags.GetNamespaceAliasesAsync(Ct)).IsEquivalentTo(new Dictionary<string, string> { ["copyright"] = "series" });
    }

    // ---- removal --------------------------------------------------------

    [Test]
    public async Task Removing_the_alias_stops_the_redirect_and_leaves_moved_tags_alone()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync(32);
        var later = await env.CreatePostAsync(33);

        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await env.Tags.RemoveNamespaceAliasAsync("copyright", Ct);
        await env.Tags.SetPostTagsAsync(later, Names("copyright:blade_runner"), Ct);

        await Assert.That(await env.TagsOnAsync(post)).IsEquivalentTo(["series:star_wars"], CollectionOrdering.Matching);
        await Assert.That(await env.TagsOnAsync(later)).IsEquivalentTo(["copyright:blade_runner"], CollectionOrdering.Matching);
    }
}
