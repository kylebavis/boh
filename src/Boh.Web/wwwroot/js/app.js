// Theme toggle: cycles auto -> light -> dark -> auto.
//
// "auto" is represented by the *absence* of data-theme, which is what lets Pico's
// prefers-color-scheme rules take over. data-theme-choice always reflects the user's
// selection so CSS can show the matching icon even in the auto case.
//
// The initial attributes are set by an inline script in <head>; this file only handles
// interaction and label sync, so it is safe to load deferred.
(function () {
    'use strict';

    var STORAGE_KEY = 'boh:theme';
    var ORDER = ['auto', 'light', 'dark'];
    var LABELS = {
        auto: 'Theme: follow system',
        light: 'Theme: light',
        dark: 'Theme: dark'
    };

    function readChoice() {
        try {
            var stored = localStorage.getItem(STORAGE_KEY);
            return stored === 'light' || stored === 'dark' ? stored : 'auto';
        } catch (e) {
            return 'auto';
        }
    }

    function writeChoice(choice) {
        try {
            if (choice === 'auto') localStorage.removeItem(STORAGE_KEY);
            else localStorage.setItem(STORAGE_KEY, choice);
        } catch (e) {
            // Storage unavailable (private mode, cookies blocked). The theme still
            // applies for this page view; it just will not persist.
        }
    }

    function apply(choice) {
        var root = document.documentElement;

        root.dataset.themeChoice = choice;
        if (choice === 'auto') delete root.dataset.theme;
        else root.dataset.theme = choice;

        var button = document.getElementById('theme-toggle');
        if (button) {
            button.setAttribute('aria-label', LABELS[choice]);
            button.setAttribute('title', LABELS[choice]);
        }
    }

    function init() {
        // Sync the button's label with whatever the inline script already applied.
        apply(readChoice());

        var button = document.getElementById('theme-toggle');
        if (!button) return;

        button.addEventListener('click', function () {
            var next = ORDER[(ORDER.indexOf(readChoice()) + 1) % ORDER.length];
            writeChoice(next);
            apply(next);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

// Tag autocomplete: clicking a suggestion replaces the token being typed rather than the
// whole field, so a partly-written multi-tag query survives.
//
// Fields marked data-suggest-single hold exactly one tag (the tag-admin forms), so there a
// suggestion replaces the whole value.
(function () {
    'use strict';

    function inputFor(panel) {
        return document.querySelector('[data-suggest-for="#' + panel.id + '"]');
    }

    /*
       Every completing input sends its term as `q`, whatever the field is actually named.
       htmx would otherwise send the field's own name — `from`, `canonical`, `child` — and the
       endpoint would find no term and return nothing. Normalising here rather than teaching
       the endpoint six field names keeps the two ends from having to agree on form details.
    */
    document.addEventListener('htmx:configRequest', function (event) {
        var input = event.detail.elt;
        if (!input || !input.matches || !input.matches('[data-suggest-for]')) return;

        var params = event.detail.parameters;
        var name = input.getAttribute('name');

        // htmx 2 hands over a FormData; earlier versions a plain object.
        if (params && typeof params.set === 'function') {
            if (name) params.delete(name);
            params.set('q', input.value);
        } else if (params) {
            if (name) delete params[name];
            params.q = input.value;
        }
    });

    // Reflects dropdown state for screen readers on the fields that declare a combobox role.
    document.addEventListener('htmx:afterSwap', function (event) {
        var panel = event.target;
        if (!panel || !panel.classList || !panel.classList.contains('suggestions')) return;

        var input = inputFor(panel);
        if (input && input.hasAttribute('aria-expanded')) {
            input.setAttribute('aria-expanded', panel.childElementCount > 0 ? 'true' : 'false');
        }
    });

    function close(panel) {
        panel.innerHTML = '';

        var input = inputFor(panel);
        if (input && input.hasAttribute('aria-expanded')) {
            input.setAttribute('aria-expanded', 'false');
        }
    }

    function replaceLastToken(value, replacement) {
        var trailing = /\s$/.test(value);
        var tokens = value.split(/\s+/).filter(function (t) { return t.length > 0; });

        // A trailing space means the caret is on a fresh token, so nothing gets replaced.
        if (!trailing && tokens.length > 0) tokens.pop();

        tokens.push(replacement);
        return tokens.join(' ') + ' ';
    }

    // Delegated so it keeps working after HTMX swaps the suggestion list.
    document.addEventListener('click', function (event) {
        var button = event.target.closest('.suggestion');
        if (!button) return;

        var panel = button.closest('.suggestions');
        if (!panel) return;

        var input = inputFor(panel);
        if (!input) return;

        var tag = button.dataset.tag || '';

        // A single-tag field takes the whole value, and with no trailing space: it is submitted
        // as-is to a handler that parses one tag name, not a list.
        input.value = input.hasAttribute('data-suggest-single') ? tag : replaceLastToken(input.value, tag);

        close(panel);
        input.focus();
    });

    // Dismiss suggestions when focus moves elsewhere.
    document.addEventListener('click', function (event) {
        document.querySelectorAll('.suggestions').forEach(function (panel) {
            if (panel.contains(event.target)) return;

            var input = inputFor(panel);
            if (input && input === event.target) return;

            close(panel);
        });
    });

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape') return;
        document.querySelectorAll('.suggestions').forEach(close);
    });
})();

// Live row filter for the tag-admin tables. Client-side because every row is already in the
// document: a round trip per keystroke would be slower and no more accurate.
(function () {
    'use strict';

    function rowText(row) {
        // The actions cell is excluded deliberately — every row contains the word "Remove",
        // so including it would make that term match everything.
        return Array.prototype.filter
            .call(row.cells, function (cell) { return !cell.classList.contains('row-actions'); })
            .map(function (cell) { return cell.textContent; })
            .join(' ')
            .toLowerCase();
    }

    function apply(input) {
        var table = document.querySelector(input.dataset.filterTable);
        if (!table) return;

        var term = input.value.trim().toLowerCase();
        var rows = table.tBodies.length ? table.tBodies[0].rows : [];
        var shown = 0;
        var total = 0;
        var empty = null;

        Array.prototype.forEach.call(rows, function (row) {
            if (row.classList.contains('filter-empty')) {
                empty = row;
                return;
            }

            total++;
            var match = term === '' || rowText(row).indexOf(term) !== -1;
            row.hidden = !match;
            if (match) shown++;
        });

        if (empty) empty.hidden = shown > 0;

        var status = input.dataset.filterStatus && document.querySelector(input.dataset.filterStatus);
        if (status) {
            status.textContent = term === ''
                ? total + ' row' + (total === 1 ? '' : 's')
                : shown + ' of ' + total + ' shown';
        }
    }

    function init() {
        document.querySelectorAll('[data-filter-table]').forEach(function (input) {
            input.addEventListener('input', function () { apply(input); });

            // Escape clears rather than only closing something, since there is no dropdown here.
            input.addEventListener('keydown', function (event) {
                if (event.key !== 'Escape' || input.value === '') return;
                input.value = '';
                apply(input);
            });

            // A value restored by the browser on reload must take effect without a keystroke.
            if (input.value !== '') apply(input);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
